using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "When your opponent activates a Spell Card in their hand that targets a monster you control (Quick Effect)", "If a
/// "Dragonmaid" monster's effect is activated in the GY", "When a Spell/Trap Card, or monster effect, is activated
/// (activate this Trap)": an activation, with who, what kind, where from, which card and what it targets as details.
/// </summary>
public sealed class ActivationTrigger : BlockDefinition
{
    public const string BlockId = "when.activation";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Chain";
    public override string Name => "A card or effect is activated (who, what, where, which card, targets)";
    public override string Sentence => "{mode} {who} activates {what} {from} {card} {targets} {target_cards} {includes} {responds} {responds_card}, {zone} {damage_step}";
    public override string Help => "\"When ... (Quick Effect)\" responds in the chain and can negate it; \"If ...\" is a trigger that happens after. " +
                                   "For a Spell or Trap, \"Activate this card\" responds with the card itself, like Solemn Judgment.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "How",
            new("quick", "When", "When ... (Quick Effect, in the chain)"), new("trigger", "If", "If ... (a trigger)"),
            new("activate", "When", "When ... (activate this Spell/Trap Card in the chain)")),
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
            new("trap_any", "a Trap Card or effect"),
            new("card_or_monster", "a Spell/Trap Card, or monster effect")),
        ParamSpec.Choice("from", "Activated in",
            new("any", "", "anywhere"), new("hand", "in the hand"), new("field", "on the field"), new("mzone", "in the Monster Zone"),
            new("szone", "in the Spell & Trap Zone"), new("gy", "in the GY"), new("banished", "while banished"), new("hand_gy", "in the hand or GY"),
            new("field_gy", "on the field or in the GY"), new("gy_banished", "in the GY or while banished"), new("hand_field", "in the hand or on the field")),
        ParamSpec.Cards("card", "Which card (any)", () => new CardFilter { CardType = FilterCardType.Any, Zones = Zone.None }),
        ParamSpec.Choice("targets", "That targets",
            new("none", "", "(anything)"), new("your_card", "that targets a card(s) you control"), new("your_monster", "that targets a monster(s) you control"),
            new("this_card", "that targets this card"), new("any_target", "that targets a card(s)"), new("on_field", "that targets a card(s) on the field"),
            new("cards", "that targets:", "that targets a card(s) like this:")),
        ParamSpec.Cards("target_cards", "Targeting", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.Field | Zone.Graveyard | Zone.Banished)
            .ShowIf(b => b.Values.GetValueOrDefault("targets") == "cards"),
        ParamSpec.Choice("includes", "That includes",
            new("none", "", "(anything)"),
            new("special_summon", "that includes an effect that Special Summons a monster(s)"),
            new("search", "that includes an effect that adds a card(s) from the Deck to the hand"),
            new("destroy", "that includes an effect that destroys a card(s)"),
            new("damage_you", "that would inflict damage to you"),
            new("damage_opponent", "that would inflict damage to your opponent"),
            // Ash Blossom & Joyous Spring's three, any of them; Karin's "Special Summons a monster(s) from the Deck".
            new("deck", "that includes any of these effects", "that adds from the Deck to the hand, Special Summons from the Deck, or sends from the Deck to the GY (Ash Blossom)"),
            new("deck_special", "that includes an effect that Special Summons a monster(s) from the Deck")),
        // Virakam: "When your opponent activates a card or effect in response to your Fusion Monster's effect activation".
        ParamSpec.Choice("responds", "In response to", new("none", "", "(anything)"), new("yours", "in response to your card's effect activation", "your card's effect activation:")),
        ParamSpec.Cards("responds_card", "Whose effect", () => new CardFilter { CardType = FilterCardType.Monster, Zones = Zone.None })
            .ShowIf(b => b.Values.GetValueOrDefault("responds") == "yours"),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices).ShowIf(b => b.Values.GetValueOrDefault("mode") != "activate"),
        ParamSpec.Choice("damage_step", "In the Damage Step",
            new("no", "", "can't be used in the Damage Step"), new("yes", "", "can also be used in the Damage Step"),
            new("step", "", "can also be used in the Damage Step, until damage calculation")),
        ParamSpec.Flag("optional", "optional (\"You can\")", true).ShowIf(b => b.Values.GetValueOrDefault("mode") == "trigger"),
    ];

    private static bool AnyCard(CardFilter f) => FilterCompiler.Conditions(f).Count == 0 && f.CardCode == 0 && !f.ExcludeThisCard;

    /// <summary>"your opponent activates a Spell Card in their hand that targets a monster you control", "a monster effect is activated".</summary>
    private static string Event(BlockArgs args)
    {
        var who = args.Choice("who");
        var f = args.Cards("card");
        var kind = args.Choice("what");
        var what = kind switch
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
            "card_or_monster" => "a Spell/Trap Card, or monster effect",
            _ => "a card or effect",
        };
        // "a "Dragonmaid" monster's effect", "a Link Monster's effect", "another card or its effect".
        // "an "F.A." Spell/Trap Card or effect": an archetype alone goes in front.
        if (f.Archetype != 0 && f.Archetype2 == 0 && f.CardCode == 0 && !f.ExcludeThisCard && FilterCompiler.Conditions(f).Count == 1
            && kind is not ("monster" or "effect" or "any" or "card_or_monster"))
        {
            var arch = args.Pack?.ArchetypeName(f.Archetype) ?? ArchetypeNames.Find(f.Archetype) ?? $"0x{f.Archetype:x}";
            what = $"a \"{arch}\" {what[(what.IndexOf(' ') + 1)..]}";
        }
        else if (!AnyCard(f))
        {
            var noun = A(FilterText.Describe(f, args.Pack, false, "", withLocation: false));
            what = kind switch
            {
                "monster" => $"{noun}'s effect",
                "effect" or "any" => $"{noun} or its effect",
                _ => $"{what} that is {noun}",
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
            "field_gy" => $" on the field or in {their} GY",
            "gy_banished" => $" in {their} GY or while it is banished",
            "hand_field" => $" in {their} hand or on the field",
            _ => "",
        };
        var targets = args.Choice("targets") switch
        {
            "your_card" => " that targets a card(s) you control",
            "your_monster" => " that targets a monster(s) you control",
            "this_card" => " that targets this card",
            "any_target" => " that targets a card(s)",
            "on_field" => " that targets a card(s) on the field",
            "cards" => $" that targets {TargetNoun(args.Cards("target_cards"), args.Pack)}",
            _ => "",
        };
        var includes = args.Choice("includes") switch
        {
            "special_summon" => " that includes an effect that Special Summons a monster(s)",
            "search" => " that includes an effect that adds a card(s) from the Deck to the hand",
            "destroy" => " that includes an effect that destroys a card(s)",
            "damage_you" => " that would inflict damage to you",
            "damage_opponent" => " that would inflict damage to your opponent",
            "deck" => " that includes any of these effects",
            "deck_special" => " that includes an effect that Special Summons a monster(s) from the Deck",
            _ => "",
        };
        // "in response to your Fusion Monster's effect activation".
        if (args.Choice("responds") == "yours")
            includes += $" in response to your {FilterText.Describe(args.Cards("responds_card"), args.Pack, false, "", withLocation: false, withExcept: false)}'s effect activation";
        // "When a Spell/Trap Card, or monster effect, is activated".
        if (who == "either") return kind == "card_or_monster" && AnyCard(f) ? $"a Spell/Trap Card, or monster effect, is activated{from}{targets}{includes}" : $"{what} is activated{from}{targets}{includes}";
        var subject = who == "you" ? "you activate" : "your opponent activates";
        return $"{subject} {what}{from}{targets}{includes}";
    }

    /// <summary>"a monster(s) you control", "a "Gem-Knight" monster(s) you control", "a card(s) in your GY".</summary>
    private static string TargetNoun(CardFilter f, CardPack? pack)
    {
        var noun = FilterText.Describe(f, pack, false, "", withLocation: false, withExcept: false) + "(s)";
        var where = f.Zones switch
        {
            Zone.MonsterZone or Zone.Field => f.Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => " on the field" },
            Zone.SpellTrapZone => f.Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => " on the field" },
            Zone.Graveyard => f.Owner switch { CardOwner.You => " in your GY", CardOwner.Opponent => " in your opponent's GY", _ => " in a GY" },
            Zone.Banished => " that is banished",
            _ => "",
        };
        return A(noun) + where;
    }

    private static string A(string noun) => noun.StartsWith("other ", StringComparison.Ordinal) ? "another " + noun[6..]
        : noun.StartsWith('"') || !"AEIOUaeiou".Contains(noun[0]) ? "a " + noun : "an " + noun;

    /// <summary>Only the event; the text generator adds "(Quick Effect)" after any conditions, as card text does.</summary>
    public override string Describe(BlockArgs args) => args.Choice("mode") == "trigger" ? $"If {Event(args)}" : $"When {Event(args)}";

    /// <summary>Ash Blossom's "any of these effects", listed after the effect as its card does.</summary>
    public override string Afterword(BlockArgs args) => args.Choice("includes") == "deck"
        ? string.Concat(new[] { "Add a card from the Deck to the hand.", "Special Summon from the Deck.", "Send a card from the Deck to the GY." }.Select(o => EffectTextGenerator.OptionLine + o))
        : "";

    public override EventPhrase? Phrase(BlockArgs args) => args.Choice("mode") == "trigger" ? new("If", Event(args)) : null;

    /// <summary>True when the effect reads "(Quick Effect)".</summary>
    public static bool IsQuick(BlockInstance b) => b.Values.GetValueOrDefault("mode", "quick") == "quick";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var mode = ctx.Args.Choice("mode");
        switch (mode)
        {
            case "activate":
                if (ctx.Card.IsMonster) e.Error("\"Activate this card\" is for Spell and Trap cards. Monsters use \"(Quick Effect)\".");
                e.Types.Add("EFFECT_TYPE_ACTIVATE");
                e.IsActivation = true;
                break;
            case "quick":
                e.Types.Add("EFFECT_TYPE_QUICK_O");
                e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
                break;
            default:
                var optional = ctx.Args.Bool("optional");
                e.Types.Add("EFFECT_TYPE_FIELD");
                e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
                e.AddProperty("EFFECT_FLAG_DELAY");
                e.IsMandatory = !optional;
                e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
                break;
        }
        // Negations and other responses can be used in the Damage Step, as their cards say.
        switch (ctx.Args.Choice("damage_step"))
        {
            case "yes": e.AddProperty("EFFECT_FLAG_DAMAGE_STEP"); e.AddProperty("EFFECT_FLAG_DAMAGE_CAL"); break;
            case "step": e.AddProperty("EFFECT_FLAG_DAMAGE_STEP"); break;
        }
        e.Code = "EVENT_CHAINING";
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
            "card_or_monster" => "(re:IsActiveType(TYPE_MONSTER) or re:IsHasType(EFFECT_TYPE_ACTIVATE))",
            _ => "true",
        });
        var loc = ctx.Args.Choice("from") switch
        {
            "hand" => "LOCATION_HAND", "field" => "LOCATION_ONFIELD", "mzone" => "LOCATION_MZONE", "szone" => "LOCATION_SZONE",
            "gy" => "LOCATION_GRAVE", "banished" => "LOCATION_REMOVED", "hand_gy" => "LOCATION_HAND+LOCATION_GRAVE",
            "field_gy" => "LOCATION_ONFIELD+LOCATION_GRAVE", "gy_banished" => "LOCATION_GRAVE+LOCATION_REMOVED", "hand_field" => "LOCATION_HAND+LOCATION_ONFIELD",
            _ => null,
        };
        if (loc is not null) tests.Add($"Duel.GetChainInfo(ev,CHAININFO_TRIGGERING_LOCATION)&({loc})~=0");
        var card = ctx.Args.Cards("card").Clone();
        card.Zones = Zone.None;
        if (FilterCompiler.Conditions(card).Count > 0 || card.CardCode != 0) tests.Add($"{ctx.Script.Filter(card, null, "actfilter").Function}(re:GetHandler(),e,tp)");
        // "another card's effect": not this card.
        if (card.ExcludeThisCard) tests.Add("re:GetHandler()~=e:GetHandler()");
        var targets = ctx.Args.Choice("targets");
        if (targets != "none")
        {
            var test = targets switch
            {
                "your_card" => "tc:IsControler(tp) and tc:IsOnField()",
                "your_monster" => "tc:IsControler(tp) and tc:IsLocation(LOCATION_MZONE)",
                "this_card" => "tc==e:GetHandler()",
                "on_field" => "tc:IsOnField()",
                "cards" => TargetTest(ctx),
                _ => "true",
            };
            var fn = ctx.Script.AddFunction("acttgfilter", "tc,e,tp", [$"return {test}"]);
            tests.Add("re:IsHasProperty(EFFECT_FLAG_CARD_TARGET)");
            tests.Add($"Duel.GetChainInfo(ev,CHAININFO_TARGET_CARDS) and Duel.GetChainInfo(ev,CHAININFO_TARGET_CARDS):IsExists({fn},1,nil,e,tp)");
        }
        tests.Add(ctx.Args.Choice("includes") switch
        {
            "special_summon" => "re:IsHasCategory(CATEGORY_SPECIAL_SUMMON)",
            // Drawing adds cards from the Deck to the hand too (Dominus Purge).
            "search" => "(re:IsHasCategory(CATEGORY_SEARCH) or re:IsHasCategory(CATEGORY_DRAW))",
            "destroy" => "re:IsHasCategory(CATEGORY_DESTROY)",
            "damage_you" => "aux.damcon1(e,tp,eg,ep,ev,re,r,rp)",
            "damage_opponent" => "aux.damcon1(e,1-tp,eg,ep,ev,re,r,rp)",
            "deck" => $"{FromDeckCheck(ctx.Script)}(ev,re)",
            "deck_special" => $"{DeckSpecialCheck(ctx.Script)}(ev)",
            _ => "true",
        });
        if (ctx.Args.Choice("responds") == "yours")
        {
            // The chain link before it: an effect of your card that is activated (the filter's card).
            var card2 = ctx.Args.Cards("responds_card").Clone();
            card2.Zones = Zone.None;
            var kind = card2.CardType == FilterCardType.Monster ? " and te:IsActiveType(TYPE_MONSTER)" : "";
            var test = FilterCompiler.Conditions(card2).Count > 0 || card2.CardCode != 0 ? $" and {ctx.Script.Filter(card2, null, "respfilter").Function}(te:GetHandler(),e,tp)" : "";
            var resp = ctx.Script.AddFunction("respcheck", "e,tp,ev",
            [
                "if ev<2 then return false end",
                "local te,p=Duel.GetChainInfo(ev-1,CHAININFO_TRIGGERING_EFFECT,CHAININFO_TRIGGERING_PLAYER)",
                $"return te and p==tp{kind}{test}",
            ]);
            tests.Add($"{resp}(e,tp,ev)");
        }
        // The checks need the chain link (ev), so they go into a function of their own.
        var con = ctx.Script.AddFunction("actcon", "e,tp,eg,ep,ev,re,r,rp", [$"return {string.Join(" and ", tests.Where(t => t != "true").DefaultIfEmpty("true"))}"]);
        e.Conditions.Add($"{con}(e,tp,eg,ep,ev,re,r,rp)");
    }

    /// <summary>
    /// Ash Blossom's check, as its script has it: an effect that Special Summons from the Deck or sends from the Deck to the
    /// GY, or that searches, draws or mills.
    /// </summary>
    private static string FromDeckCheck(ScriptBuilder script) => script.AddFunction("deckcheck", "ev,re",
    [
        "local ex2,g2,gc2,dp2,dv2=Duel.GetOperationInfo(ev,CATEGORY_SPECIAL_SUMMON)",
        "local ex3,g3,gc3,dp3,dv3=Duel.GetOperationInfo(ev,CATEGORY_TOGRAVE)",
        "return (ex2 and bit.band(dv2,LOCATION_DECK)==LOCATION_DECK) or (ex3 and bit.band(dv3,LOCATION_DECK)==LOCATION_DECK)",
        "\tor re:IsHasCategory(CATEGORY_DRAW) or re:IsHasCategory(CATEGORY_SEARCH) or re:IsHasCategory(CATEGORY_DECKDES)",
    ]);

    /// <summary>"That includes an effect that Special Summons a monster(s) from the Deck": the chain link says it Special Summons from there.</summary>
    private static string DeckSpecialCheck(ScriptBuilder script) => script.AddFunction("deckspcheck", "ev",
    [
        "local ex,g,gc,dp,dv=Duel.GetOperationInfo(ev,CATEGORY_SPECIAL_SUMMON)",
        "return ex and (bit.band(dv,LOCATION_DECK)~=0 or g and g:IsExists(Card.IsLocation,1,nil,LOCATION_DECK))",
    ]);

    /// <summary>The picked target cards: where they are, who controls them and what they are.</summary>
    private static string TargetTest(BlockContext ctx)
    {
        var f = ctx.Args.Cards("target_cards");
        var parts = new List<string>();
        if (f.Zones != Zone.None) parts.Add($"tc:IsLocation({Lua.Locations(f.Zones)})");
        // Cards in a GY or banished are controlled by their owner.
        if (f.Owner == CardOwner.You) parts.Add("tc:IsControler(tp)");
        if (f.Owner == CardOwner.Opponent) parts.Add("tc:IsControler(1-tp)");
        if (f.ExcludeThisCard) parts.Add("tc~=e:GetHandler()");
        var conditions = FilterCompiler.Conditions(f);
        if (conditions.Count > 0) parts.Add($"{ctx.Script.Filter(f, null, "acttgcards").Function}(tc,e,tp)");
        return parts.Count == 0 ? "true" : string.Join(" and ", parts);
    }
}

/// <summary>
/// "When an opponent's monster declares an attack", "When a "HERO" monster you control declares a direct attack", "When
/// your opponent's monster declares an attack targeting a DARK monster you control", "When an attack is declared
/// involving your Warrior monster", "When a "Blackwing" monster you control is targeted for an attack".
/// </summary>
public sealed class AttackDeclaredTrigger : BlockDefinition
{
    public const string BlockId = "when.attack_declared";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Battle";
    public override string Name => "A monster declares an attack, or is targeted for one (which attacker, which target)";
    public override string Sentence => "{mode} {attacker} declares {how} {target} {mine}, {zone}";
    public override string Help => "\"Involving your monster\" is any attack your monster makes or receives. \"Is targeted for an attack\" also counts a new target picked for an attack.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "How", new("quick", "When", "When ... (Quick Effect)"), new("trigger", "When", "When ... (a trigger)"),
            new("activate", "When", "When ... (activate this Spell/Trap Card)")),
        ParamSpec.Cards("attacker", "Which attacker", () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone)
            .ShowIf(b => b.Values.GetValueOrDefault("how") != "involving"),
        ParamSpec.Choice("how", "Attack", new("any", "an attack"), new("direct", "a direct attack"), new("targeting", "an attack targeting:"),
            new("involving", "an attack involving:", "an attack involving your monster:"), new("targeted", "is targeted for an attack:", "a monster is targeted for an attack:")),
        ParamSpec.Cards("target", "Targeting", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone, thisCard: true)
            .ShowIf(b => b.Values.GetValueOrDefault("how") is "targeting" or "targeted"),
        ParamSpec.Cards("mine", "Your monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone)
            .ShowIf(b => b.Values.GetValueOrDefault("how") == "involving"),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices).ShowIf(b => b.Values.GetValueOrDefault("mode") != "activate"),
        ParamSpec.Flag("optional", "optional (\"You can\")", true).ShowIf(b => b.Values.GetValueOrDefault("mode") == "trigger"),
    ];

    private static bool Plain(CardFilter f) => FilterCompiler.Conditions(f).All(c => c == "c:IsType(TYPE_MONSTER)") && !f.ExcludeThisCard;

    /// <summary>"an opponent's monster", "a "HERO" monster you control", "a monster".</summary>
    private static string Monster(CardFilter f, CardPack? pack)
    {
        if (f.ThisCard) return "this card";
        var noun = FilterText.Describe(f, pack, false, "", withLocation: false);
        if (f.Owner == CardOwner.Opponent) return $"an opponent's {noun}";
        var a = noun.StartsWith("other ", StringComparison.Ordinal) ? "another " + noun[6..] : (!noun.StartsWith('"') && "AEIOUaeiou".Contains(noun[0]) ? "an " : "a ") + noun;
        return f.Owner == CardOwner.You ? $"{a} you control" : a;
    }

    private static string Event(BlockArgs args)
    {
        var a = args.Cards("attacker");
        switch (args.Choice("how"))
        {
            // The Winged Dragon of Ra, Sun God of Darkness: "When an attack is declared involving this card".
            case "involving" when args.Cards("mine").ThisCard:
                return "an attack is declared involving this card";
            case "involving":
                return $"an attack is declared involving your {FilterText.Describe(args.Cards("mine"), args.Pack, false, "", withLocation: false)}";
            // Angelechy Seneschal: "If an "Angelechy" monster targets this card for an attack".
            case "targeted" when args.Cards("target").ThisCard:
                var attacker = Monster(a, args.Pack);
                if (attacker.StartsWith("a \"", StringComparison.Ordinal) && "AEIOUaeiou".Contains(attacker[3])) attacker = "an " + attacker[2..];
                return $"{(Plain(a) && a.Owner != CardOwner.You ? "an opponent's monster" : attacker)} targets this card for an attack";
            case "targeted":
                var quiet = Plain(a) && (a.Owner == CardOwner.Either || a.Owner == CardOwner.Opponent && args.Cards("target").Owner == CardOwner.You);
                var by = quiet ? "" : $" by {Monster(a, args.Pack)}";
                return $"{Monster(args.Cards("target"), args.Pack)} is targeted for an attack{by}";
        }
        var how = args.Choice("how") switch
        {
            "direct" => "a direct attack",
            "targeting" => $"an attack targeting {Monster(args.Cards("target"), args.Pack)}",
            _ => "an attack",
        };
        return $"{Monster(a, args.Pack)} declares {how}";
    }

    /// <summary>Only the event; the text generator adds "(Quick Effect)" after any conditions, as card text does.</summary>
    public override string Describe(BlockArgs args) => $"When {Event(args)}";

    public override EventPhrase? Phrase(BlockArgs args) => args.Choice("mode") == "trigger" ? new("When", Event(args)) : null;

    /// <summary>True when the effect reads "(Quick Effect)".</summary>
    public static bool IsQuick(BlockInstance b) => b.Values.GetValueOrDefault("mode", "quick") == "quick";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        switch (ctx.Args.Choice("mode"))
        {
            case "quick":
                e.Types.Add("EFFECT_TYPE_QUICK_O");
                e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
                break;
            case "activate":
                if (ctx.Card.IsMonster) e.Error("\"Activate this card\" is for Spell and Trap cards. Monsters use \"(Quick Effect)\".");
                e.Types.Add("EFFECT_TYPE_ACTIVATE");
                e.IsActivation = true;
                break;
            default:
                // "When ...": it activates right away, so it can miss its timing.
                var optional = ctx.Args.Bool("optional");
                e.Types.Add("EFFECT_TYPE_FIELD");
                e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
                e.IsMandatory = !optional;
                e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
                break;
        }
        var how = ctx.Args.Choice("how");
        e.Code = how == "targeted" ? "EVENT_BE_BATTLE_TARGET" : "EVENT_ATTACK_ANNOUNCE";
        var tests = new List<string>();
        if (how == "involving")
        {
            // Your monster that battles: the attacker if it is yours, otherwise the attack target.
            var mine = ctx.Args.Cards("mine");
            var mf = ctx.Script.Filter(mine, null, "battlerfilter");
            tests.Add($"Duel.GetBattleMonster(tp)~=nil and {mf.Function}(Duel.GetBattleMonster(tp),e,tp)");
            if (mine.ExcludeThisCard) tests.Add("Duel.GetBattleMonster(tp)~=e:GetHandler()");
            e.Conditions.AddRange(tests);
            return;
        }
        var a = ctx.Args.Cards("attacker");
        if (how != "targeted" || !Plain(a) || a.Owner != CardOwner.Either)
        {
            var af = ctx.Script.Filter(a, null, "attackerfilter");
            var owner = a.Owner switch { CardOwner.You => " and Duel.GetAttacker():IsControler(tp)", CardOwner.Opponent => " and Duel.GetAttacker():IsControler(1-tp)", _ => "" };
            tests.Add($"{af.Function}(Duel.GetAttacker(),e,tp){owner}");
            if (a.ExcludeThisCard) tests.Add("Duel.GetAttacker()~=e:GetHandler()");
        }
        switch (how)
        {
            case "direct": tests.Add("Duel.GetAttackTarget()==nil"); break;
            // This card itself is the attack's target.
            case "targeting" or "targeted" when ctx.Args.Cards("target").ThisCard:
                Zones.RequireMonster(ctx, "This card is targeted for an attack");
                tests.Add("Duel.GetAttackTarget()==e:GetHandler()");
                break;
            case "targeting" or "targeted":
            {
                var t = ctx.Args.Cards("target");
                var tf = ctx.Script.Filter(t, null, "attackedfilter");
                var towner = t.Owner switch { CardOwner.You => " and Duel.GetAttackTarget():IsControler(tp)", CardOwner.Opponent => " and Duel.GetAttackTarget():IsControler(1-tp)", _ => "" };
                tests.Add($"Duel.GetAttackTarget()~=nil and {tf.Function}(Duel.GetAttackTarget(),e,tp){towner}");
                if (t.ExcludeThisCard) tests.Add("Duel.GetAttackTarget()~=e:GetHandler()");
                break;
            }
        }
        e.Conditions.AddRange(tests);
    }
}

/// <summary>"When your opponent would Special Summon a monster(s)", "When a monster(s) would be Summoned": a Summon that can still be negated.</summary>
public sealed class SummonAttemptTrigger : BlockDefinition
{
    public const string BlockId = "when.summon_attempt";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Chain";
    public override string Name => "A monster would be Summoned (who, how, which monsters)";
    public override string Sentence => "{mode} {who} would {how} Summon {cards} {count}, {zone} {check}";
    public override string Help => "The moment before a Summon succeeds, when \"Negate the Summon\" works, like Solemn Strike or Black Horn of Heaven.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "How", new("quick", "When", "When ... (Quick Effect)"), new("activate", "When", "When ... (activate this Spell/Trap Card)")),
        ParamSpec.Choice("who", "Who", new("opponent", "your opponent"), new("you", "you"), new("either", "a player")),
        ParamSpec.Choice("how", "Summon", new("special", "Special"), new("normal", "Normal"), new("flip", "Flip"), new("any", "", "Normal, Flip or Special")),
        ParamSpec.Cards("cards", "Which monsters (any)", () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster }),
        ParamSpec.Choice("count", "How many", new("any", "", "any number"), new("one", "exactly 1", "exactly 1 monster")),
        // Both mean "while no chain is building"; scripts write it two ways, which differ while a card is being activated.
        ParamSpec.Choice("check", "Chain check", new("current", "", "no chain (Duel.GetCurrentChain)"), new("ready", "", "no chain ready (aux.NegateSummonCondition)")),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices).ShowIf(b => b.Values.GetValueOrDefault("mode") == "quick"),
    ];

    private static string Event(BlockArgs args)
    {
        var how = args.Choice("how");
        var f = args.Cards("cards");
        var plural = how is "special" or "any" && args.Choice("count") != "one";
        string noun;
        if (args.Choice("count") == "one") noun = "exactly 1 " + Noun(f, args.Pack, false);
        else
        {
            var n = Noun(f, args.Pack, false);
            noun = (!n.StartsWith('"') && "AEIOUaeiou".Contains(n[0]) ? "an " : "a ") + n + (plural ? "(s)" : "");
        }
        var kind = how switch { "normal" => "Normal ", "flip" => "Flip ", "special" => "Special ", _ => "" };
        return args.Choice("who") switch
        {
            "opponent" => $"your opponent would {kind}Summon {noun}",
            "you" => $"you would {kind}Summon {noun}",
            _ => $"{noun} would be {kind}Summoned",
        };
    }

    private static string Noun(CardFilter f, CardPack? pack, bool plural) => FilterText.Describe(f, pack, plural, "", withLocation: false);

    public override string Describe(BlockArgs args) => $"When {Event(args)}";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (ctx.Args.Choice("mode") == "activate")
        {
            if (ctx.Card.IsMonster) e.Error("\"Activate this card\" is for Spell and Trap cards. Monsters use \"(Quick Effect)\".");
            e.Types.Add("EFFECT_TYPE_ACTIVATE");
            e.IsActivation = true;
        }
        else
        {
            e.Types.Add("EFFECT_TYPE_QUICK_O");
            e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
        }
        switch (ctx.Args.Choice("how"))
        {
            case "normal": e.Code = "EVENT_SUMMON"; break;
            case "flip": e.Code = "EVENT_FLIP_SUMMON"; break;
            case "special": e.Code = "EVENT_SPSUMMON"; break;
            default:
                e.Code = "EVENT_SUMMON";
                e.ExtraCodes.Add("EVENT_FLIP_SUMMON");
                e.ExtraCodes.Add("EVENT_SPSUMMON");
                break;
        }
        // A Summon can be negated only while no chain is building.
        e.Conditions.Add(ctx.Args.Choice("check") == "ready" ? "aux.NegateSummonCondition()" : "Duel.GetCurrentChain()==0");
        switch (ctx.Args.Choice("who"))
        {
            case "opponent": e.Conditions.Add("ep==1-tp"); break;
            case "you": e.Conditions.Add("ep==tp"); break;
        }
        if (ctx.Args.Choice("count") == "one") e.Conditions.Add("eg:GetCount()==1");
        var f = ctx.Args.Cards("cards").Clone();
        f.Zones = Zone.None;
        var conditions = FilterCompiler.Conditions(f);
        if (conditions.Any(c => c != "c:IsType(TYPE_MONSTER)") || f.CardCode != 0)
            e.Conditions.Add($"eg:IsExists({ctx.Script.Filter(f, null, "sumfilter").Function},1,nil,e,tp)");
    }
}

/// <summary>
/// "During damage calculation, if your "roid" monster battles", "If this card attacks a Defense Position monster, before
/// damage calculation", "At the end of the Damage Step, if this card battled an opponent's monster": a moment of a
/// battle, and whose battle it is.
/// </summary>
public sealed class BattleWindowTrigger : BlockDefinition
{
    public const string BlockId = "when.battle";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Battle";
    public override string Name => "A moment of a battle (start of the Damage Step, damage calculation, after it...)";
    public override string Sentence => "{mode} {window}, if {who} {mine} battles {versus} {vs} {relate}, {zone}";
    public override string Help => "The steps of a battle: the start of the Damage Step, before damage calculation, during it, after it, and the end of the Damage Step.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "How", new("quick", "", "(Quick Effect)"), new("trigger", "", "a trigger"), new("activate", "", "activate this Spell/Trap Card")),
        ParamSpec.Choice("window", "When",
            new("calc", "during damage calculation"),
            new("start", "at the start of the Damage Step"),
            new("before", "before damage calculation"),
            new("after", "after damage calculation"),
            new("end", "at the end of the Damage Step")),
        ParamSpec.Choice("who", "Whose battle",
            new("this_card", "this card battles"), new("attacks", "this card attacks"), new("attacked", "this card is attacked"),
            new("yours", "your monster battles:", "a monster you control battles:"), new("any", "", "any battle"),
            new("damage_you", "you would take battle damage", "you would take battle damage (damage calculation)")),
        ParamSpec.Cards("mine", "Your monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone)
            .ShowIf(b => b.Values.GetValueOrDefault("who") == "yours"),
        ParamSpec.Choice("versus", "Against",
            new("any", "", "anything (or a direct attack)"), new("monster", "a monster"), new("opponent", "an opponent's monster"), new("cards", "a monster like this:")),
        ParamSpec.Cards("vs", "The monster it battles", () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone)
            .ShowIf(b => b.Values.GetValueOrDefault("versus") == "cards"),
        // Scripts often add one of these checks for the later steps; card text doesn't say them.
        ParamSpec.Choice("relate", "Still in the battle",
            new("any", "", "even if it left the battle"), new("battle", "", "only while it is still in the battle"),
            new("battle_or_destroyed", "", "while still in the battle, or destroyed by it")),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices).ShowIf(b => b.Values.GetValueOrDefault("mode") != "activate"),
        ParamSpec.Flag("optional", "optional (\"You can\")", true).ShowIf(b => b.Values.GetValueOrDefault("mode") == "trigger"),
    ];

    /// <summary>Whose battle checks this card: an effect on this card itself ("If this card battles ...").</summary>
    private static bool OnThisCard(string who) => who is "this_card" or "attacks" or "attacked";

    /// <summary>"this card battles an opponent's monster", "your EARTH Warrior monster battles", "this card attacks a Defense Position monster".</summary>
    private static string? Clause(BlockArgs args, bool past)
    {
        var who = args.Choice("who");
        if (who == "any") return null;
        if (who == "damage_you") return "you would take battle damage";
        var subject = who == "yours" ? "your " + FilterText.Describe(args.Cards("mine"), args.Pack, false, "", withLocation: false) : "this card";
        var verb = who switch
        {
            "attacks" => past ? "attacked" : "attacks",
            "attacked" => past ? "was attacked" : "is attacked",
            _ => past ? "battled" : "battles",
        };
        var against = args.Choice("versus") switch
        {
            "monster" => " a monster",
            "opponent" => " an opponent's monster",
            "cards" => " " + A(FilterText.Describe(args.Cards("vs"), args.Pack, false, "", withLocation: false)),
            _ => "",
        };
        if (who == "attacked" && against.Length > 0) against = " by" + against;
        return $"{subject} {verb}{against}";
    }

    private static string A(string noun) => (!noun.StartsWith('"') && "AEIOUaeiou".Contains(noun[0]) ? "an " : "a ") + noun;

    public override string Describe(BlockArgs args)
    {
        var window = args.Choice("window");
        var clause = Clause(args, window == "end");
        return (window, clause) switch
        {
            ("start", null) => "At the start of the Damage Step",
            ("start", _) => $"At the start of the Damage Step, if {clause}",
            ("end", null) => "At the end of the Damage Step",
            ("end", _) => $"At the end of the Damage Step, if {clause}",
            ("calc", null) => "During damage calculation",
            ("calc", _) when args.Choice("who") == "damage_you" => $"During damage calculation, if {clause}",
            (_, null) => window == "before" ? "Before damage calculation" : "After damage calculation",
            _ => $"If {clause}, {Window(window)}",
        };
    }

    private static string Window(string window) => window switch
    {
        "before" => "before damage calculation",
        "after" => "after damage calculation",
        _ => "during damage calculation",
    };

    public static string Code(string window) => window switch
    {
        "start" => "EVENT_BATTLE_START",
        "before" => "EVENT_BATTLE_CONFIRM",
        "after" => "EVENT_BATTLED",
        "end" => "EVENT_DAMAGE_STEP_END",
        _ => "EVENT_PRE_DAMAGE_CALCULATE",
    };

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var mode = ctx.Args.Choice("mode");
        var who = ctx.Args.Choice("who");
        var window = ctx.Args.Choice("window");
        if (OnThisCard(who)) Zones.RequireMonster(ctx, Name);
        var single = mode == "trigger" && OnThisCard(who);
        switch (mode)
        {
            case "activate":
                if (ctx.Card.IsMonster) e.Error("\"Activate this card\" is for Spell and Trap cards. Monsters use \"(Quick Effect)\".");
                if (OnThisCard(who)) e.Error("A Spell or Trap Card doesn't battle: pick whose battle it is.");
                e.Types.Add("EFFECT_TYPE_ACTIVATE");
                e.IsActivation = true;
                break;
            case "quick":
                e.Types.Add("EFFECT_TYPE_QUICK_O");
                e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
                break;
            default:
                var optional = ctx.Args.Bool("optional");
                // A trigger on this card's own battle is the card's own event; any other battle is watched from where it is.
                e.Types.Add(single ? "EFFECT_TYPE_SINGLE" : "EFFECT_TYPE_FIELD");
                e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
                e.IsMandatory = !optional;
                if (!single) e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
                break;
        }
        e.Code = Code(window);
        if (who == "damage_you")
        {
            if (window != "calc") e.Warn("\"You would take battle damage\" is known during damage calculation.");
            e.Conditions.Add("Duel.GetBattleDamage(tp)>0");
            return;
        }
        if (who == "any") return;
        // The battling monster and the one it battles.
        var (self, other) = who switch
        {
            "yours" => ("Duel.GetBattleMonster(tp)", "Duel.GetBattleMonster(1-tp)"),
            "attacks" => ("e:GetHandler()", "Duel.GetAttackTarget()"),
            "attacked" => ("e:GetHandler()", "Duel.GetAttacker()"),
            _ => ("e:GetHandler()", "e:GetHandler():GetBattleTarget()"),
        };
        switch (who)
        {
            case "attacks": e.Conditions.Add("Duel.GetAttacker()==e:GetHandler()"); break;
            case "attacked": e.Conditions.Add("Duel.GetAttackTarget()==e:GetHandler()"); break;
            // Its own event says this card battles; from elsewhere it has to be checked.
            case "this_card" when !single: e.Conditions.Add("(Duel.GetAttacker()==e:GetHandler() or Duel.GetAttackTarget()==e:GetHandler())"); break;
            case "yours":
            {
                var mine = ctx.Args.Cards("mine");
                e.Conditions.Add($"Duel.GetBattleMonster(tp)~=nil and {ctx.Script.Filter(mine, null, "battlerfilter").Function}(Duel.GetBattleMonster(tp),e,tp)");
                if (mine.ExcludeThisCard) e.Conditions.Add("Duel.GetBattleMonster(tp)~=e:GetHandler()");
                break;
            }
        }
        switch (ctx.Args.Choice("versus"))
        {
            case "monster": e.Conditions.Add($"{other}~=nil"); break;
            // Damage calculation marks both monsters when they belong to different players; before it, their controllers say so.
            case "opponent" when window is "after" or "end": e.Conditions.Add($"{self}:IsStatus(STATUS_OPPO_BATTLE)"); break;
            case "opponent": e.Conditions.Add($"{other}~=nil and {other}:IsControler(1-tp)"); break;
            case "cards":
            {
                var vs = ctx.Args.Cards("vs");
                var owner = vs.Owner switch { CardOwner.You => $" and {other}:IsControler(tp)", CardOwner.Opponent => $" and {other}:IsControler(1-tp)", _ => "" };
                e.Conditions.Add($"{other}~=nil and {ctx.Script.Filter(vs, null, "battledfilter").Function}({other},e,tp){owner}");
                break;
            }
        }
        switch (ctx.Args.Choice("relate"))
        {
            case "battle": e.Conditions.Add($"{self}:IsRelateToBattle()"); break;
            case "battle_or_destroyed": e.Conditions.Add($"({self}:IsRelateToBattle() or {self}:IsStatus(STATUS_BATTLE_DESTROYED))"); break;
        }
    }
}

/// <summary>WHEN blocks with a mode read "(Quick Effect)" when they are Quick Effects.</summary>
public static class DetailedTriggers
{
    public static bool IsQuick(BlockDefinition def, BlockInstance when) =>
        def is ActivationTrigger or AttackDeclaredTrigger or SummonAttemptTrigger or BattleWindowTrigger && when.Values.GetValueOrDefault("mode", "quick") == "quick"
        || def is CardsDetailTrigger && when.Values.GetValueOrDefault("speed") == "quick";

    /// <summary>True when the effect is this Spell/Trap Card's activation, by any WHEN block ("the turn you activate this card").</summary>
    public static bool ActivatesThisCard(EffectEvent? effect) =>
        effect?.When is { } w && (w.BlockId == "when.activate" || BlockRegistry.Find(w.BlockId) is { } def && IsCardActivation(def, w));

    /// <summary>True when the block is this Spell/Trap Card's own activation ("Activate this card"), which reads without "You can".</summary>
    public static bool IsCardActivation(BlockDefinition def, BlockInstance when) =>
        def is ActivationTrigger or AttackDeclaredTrigger or SummonAttemptTrigger or BattleWindowTrigger && when.Values.GetValueOrDefault("mode") == "activate";
}

/// <summary>
/// "If this card in your possession is destroyed by your opponent's card effect and sent to your GY", "If this face-up
/// card leaves the field", "If this Synchro Summoned card is sent from the field to the GY", "If this card is banished
/// from your GY", "If this card is sent to the GY as material for a Link Summon of a Cyberse monster": what happens to
/// this card, and the details of how.
/// </summary>
public sealed class ThisCardDetailTrigger : BlockDefinition
{
    public const string BlockId = "when.this_card_detail";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "This card";
    public override string Name => "This card is sent, destroyed, banished, moved... (where from, by what, face-up, yours)";
    public override string Sentence => "{wording} this {face} {summoned} card {yours} {event} {from} {by} {material} {summoner} {now}";
    public override string Help => "Every detail is optional: where it was, whether it was face-up and yours, what caused it, where it is now.";

    /// <summary>One thing that can happen to this card: its event and how card text says it.</summary>
    public sealed record Happening(string Key, string Code, string Text);

    public static readonly Happening[] Happenings =
    [
        new("sent_gy", "EVENT_TO_GRAVE", "is sent to the GY"),
        new("destroyed", "EVENT_DESTROYED", "is destroyed"),
        new("banished", "EVENT_REMOVE", "is banished"),
        new("leaves", "EVENT_LEAVE_FIELD", "leaves the field"),
        new("to_hand", "EVENT_TO_HAND", "is added to the hand"),
        new("to_deck", "EVENT_TO_DECK", "is returned to the Deck"),
        new("tributed", "EVENT_RELEASE", "is Tributed"),
        new("battle_destroyed", "EVENT_BATTLE_DESTROYED", "is destroyed by battle"),
        new("material", "EVENT_BE_MATERIAL", "is used as material"),
        new("drawn", "EVENT_DRAW", "is drawn"),
        new("discarded", "EVENT_DISCARD", "is discarded"),
    ];

    public static Happening Find(string key) => Happenings.FirstOrDefault(h => h.Key == key) ?? Happenings[0];

    /// <summary>Where this card was: the event's previous location.</summary>
    public static readonly (string Key, string Text, string? Lua)[] Places =
    [
        ("any", "", null),
        ("field", "the field", "LOCATION_ONFIELD"),
        ("mzone", "the Monster Zone", "LOCATION_MZONE"),
        ("szone", "the Spell & Trap Zone", "LOCATION_SZONE"),
        ("fzone", "the Field Zone", "LOCATION_FZONE"),
        ("pzone", "the Pendulum Zone", "LOCATION_PZONE"),
        ("hand", "the hand", "LOCATION_HAND"),
        ("deck", "the Deck", "LOCATION_DECK"),
        ("gy", "the GY", "LOCATION_GRAVE"),
        ("banished", "banishment", "LOCATION_REMOVED"),
        ("extra", "the Extra Deck", "LOCATION_EXTRA"),
        ("overlay", "an Xyz Monster", "LOCATION_OVERLAY"),
        ("hand_field", "the hand or field", "LOCATION_HAND+LOCATION_ONFIELD"),
        ("hand_deck", "the hand or Deck", "LOCATION_HAND+LOCATION_DECK"),
        ("field_gy", "the field or GY", "LOCATION_ONFIELD+LOCATION_GRAVE"),
        ("hand_gy", "the hand or GY", "LOCATION_HAND+LOCATION_GRAVE"),
        ("hand_mzone", "the hand or Monster Zone", "LOCATION_HAND+LOCATION_MZONE"),
        ("deck_gy", "the Deck or GY", "LOCATION_DECK+LOCATION_GRAVE"),
        ("not_field", "anywhere but the field", "not:LOCATION_ONFIELD"),
    ];

    /// <summary>What caused it, as the reasons the event's card carries.</summary>
    public static readonly (string Key, string Text, string? Lua)[] Causes =
    [
        ("any", "", null),
        ("battle", "by battle", "c:IsReason(REASON_BATTLE)"),
        ("effect", "by a card effect", "c:IsReason(REASON_EFFECT)"),
        ("battle_effect", "by battle or card effect", "c:IsReason(REASON_BATTLE+REASON_EFFECT)"),
        ("destroy", "because it was destroyed", "c:IsReason(REASON_DESTROY)"),
        ("destroy_battle_effect", "because it was destroyed by battle or card effect", "c:IsReason(REASON_DESTROY) and c:IsReason(REASON_BATTLE+REASON_EFFECT)"),
        ("destroy_effect", "because it was destroyed by a card effect", "c:IsReason(REASON_DESTROY) and c:IsReason(REASON_EFFECT)"),
        ("cost", "as a cost", "c:IsReason(REASON_COST)"),
        ("discard", "by being discarded", "c:IsReason(REASON_DISCARD)"),
        ("discard_effect", "by being discarded by a card effect", "c:IsReason(REASON_DISCARD) and c:IsReason(REASON_EFFECT)"),
        ("opponent_discard_effect", "by being discarded by an opponent's card effect", "c:GetReasonPlayer()==1-tp and c:IsReason(REASON_DISCARD) and c:IsReason(REASON_EFFECT)"),
        ("reveal", "after being excavated", "c:IsReason(REASON_REVEAL)"),
        ("summon", "for a Tribute Summon", "c:IsReason(REASON_SUMMON)"),
        ("opponent", "by an opponent's card", "c:GetReasonPlayer()==1-tp"),
        ("opponent_effect", "by an opponent's card effect", "c:GetReasonPlayer()==1-tp and c:IsReason(REASON_EFFECT)"),
        ("opponent_destroy", "because it was destroyed by an opponent's card", "c:GetReasonPlayer()==1-tp and c:IsReason(REASON_DESTROY)"),
        ("opponent_destroy_effect", "because it was destroyed by an opponent's card effect", "c:GetReasonPlayer()==1-tp and c:IsReason(REASON_DESTROY) and c:IsReason(REASON_EFFECT)"),
        ("battle_or_opponent_effect", "by battle or an opponent's card effect", "(c:IsReason(REASON_BATTLE) or c:GetReasonPlayer()==1-tp and c:IsReason(REASON_EFFECT))"),
        ("battle_or_opponent_effect_own", "by battle or an opponent's card effect while you controlled it",
            "(c:IsReason(REASON_BATTLE) or c:GetReasonPlayer()==1-tp and c:IsReason(REASON_EFFECT) and c:IsPreviousControler(tp))"),
        // Reindsurm, Aiwass: "If this card is sent to the GY, except by battle"; Ancient Gear Box: "..., except by drawing it".
        ("not_battle", "except by battle", "not c:IsReason(REASON_BATTLE)"),
        ("not_draw", "except by drawing it", "not c:IsReason(REASON_DRAW)"),
    ];

    /// <summary>A cause said after a comma: ", except by battle".</summary>
    public static bool IsException(string cause) => cause.StartsWith("except ", StringComparison.Ordinal);

    /// <summary>Where this card is now.</summary>
    public static readonly (string Key, string Text, string? Lua)[] Destinations =
    [
        ("any", "", null),
        ("gy", "and sent to the GY", "c:IsLocation(LOCATION_GRAVE)"),
        ("banished", "and banished", "c:IsLocation(LOCATION_REMOVED)"),
        ("hand", "and returned to the hand", "c:IsLocation(LOCATION_HAND)"),
        ("deck", "and put into the Deck", "c:IsLocation(LOCATION_DECK)"),
        ("extra_faceup", "and placed face-up in the Extra Deck", "c:IsLocation(LOCATION_EXTRA) and c:IsFaceup()"),
        ("gy_or_banished", "and sent to the GY or banished", "c:IsLocation(LOCATION_GRAVE+LOCATION_REMOVED)"),
        ("banished_faceup", "and banished face-up", "c:IsLocation(LOCATION_REMOVED) and c:IsFaceup()"),
        // A Pendulum Monster goes face-up to the Extra Deck instead of the GY; card text doesn't say it.
        ("faceup", "", "c:IsFaceup()"),
    ];

    public static readonly (string Key, string Label, string? Reason)[] Materials =
    [
        ("any", "", null),
        ("fusion", "Fusion", "REASON_FUSION"),
        ("synchro", "Synchro", "REASON_SYNCHRO"),
        ("xyz", "Xyz", "REASON_XYZ"),
        ("link", "Link", "REASON_LINK"),
        ("ritual", "Ritual", "REASON_RITUAL"),
        ("tribute", "Tribute", "REASON_SUMMON"),
    ];

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "What happens", Happenings.Select(h => new ChoiceOption(h.Key, h.Text)).ToArray()),
        ParamSpec.Choice("from", "From", Places.Select(p => new ChoiceOption(p.Key, p.Text, p.Key == "any" ? "anywhere" : p.Text)).ToArray()),
        ParamSpec.Choice("face", "It was", new("any", "", "face-up or face-down"), new("up", "face-up"), new("down", "face-down (Set)")),
        ParamSpec.Flag("yours", "in your possession (you controlled it)", false),
        ParamSpec.Choice("summoned", "It was Summoned", new("any", "", "any way, or not Summoned"), new("special", "Special"), new("normal", "Normal"), new("tribute", "Tribute"),
            new("fusion", "Fusion"), new("synchro", "Synchro"), new("xyz", "Xyz"), new("link", "Link"), new("ritual", "Ritual"), new("pendulum", "Pendulum")),
        ParamSpec.Choice("by", "Because of", Causes.Select(c => new ChoiceOption(c.Key, c.Text, c.Key == "any" ? "anything" : null)).ToArray()),
        ParamSpec.Choice("material", "Material for", Materials.Select(m => new ChoiceOption(m.Key, m.Label, m.Key == "any" ? "any Summon" : m.Label + " Summon")).ToArray())
            .ShowIf(b => b.Values.GetValueOrDefault("event") == "material"),
        ParamSpec.Cards("summoner", "The monster Summoned with it (any)", () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster })
            .ShowIf(b => b.Values.GetValueOrDefault("event") == "material"),
        ParamSpec.Choice("now", "Now it is", Destinations.Select(d => new ChoiceOption(d.Key, d.Text, d.Key == "any" ? "anywhere" : d.Text)).ToArray()),
        // Material that returns to the GY (after being banished face-down, for one) isn't sent there as material.
        ParamSpec.Flag("not_returned", "not when it returns to the GY", false),
        ParamSpec.Choice("wording", "Wording", new("if", "If", "If ... (can't miss its timing)"), new("when", "When", "When ... (can miss its timing)")),
        ParamSpec.Flag("optional", "optional (\"You can\")", true),
    ];

    private static string Of((string Key, string Text, string? Lua)[] table, string key) => table.FirstOrDefault(t => t.Key == key).Text ?? "";

    private static bool OnField(string from) => from is "field" or "mzone" or "szone" or "fzone" or "pzone";

    /// <summary>Where this card was, said after "this card": "this card in the Monster Zone is destroyed".</summary>
    private static string? Where(string from) => from switch
    {
        "field" => "on the field",
        "mzone" => "in the Monster Zone",
        "szone" => "in the Spell & Trap Zone",
        "fzone" => "in the Field Zone",
        "pzone" => "in the Pendulum Zone",
        "hand" => "in your hand",
        "deck" => "in your Deck",
        "gy" => "in your GY",
        "banished" => "that is banished",
        "extra" => "in your Extra Deck",
        _ => null,
    };

    /// <summary>
    /// "this face-up Synchro Summoned card in your possession is sent from the field to the GY by a card effect", "this
    /// card in the Monster Zone is destroyed by battle or card effect", "this card is destroyed by battle or card effect
    /// and sent to the GY".
    /// </summary>
    private static string Event(BlockArgs args)
    {
        var ev = args.Choice("event");
        var from = args.Choice("from");
        var face = args.Choice("face");
        var by = args.Choice("by");
        var now = args.Choice("now");
        var words = new List<string> { "this" };
        if (face == "up") words.Add("face-up");
        if (face == "down") words.Add(OnField(from) || from == "any" ? "Set" : "face-down");
        if (args.Choice("summoned") != "any") words.Add(args.Describe("summoned") + " Summoned");
        words.Add("card");
        if (args.Bool("yours") || by == "battle_or_opponent_effect_own") words.Add("in your possession");
        var place = Of(Places, from);
        var cause = by == "battle_or_opponent_effect_own" ? "by battle or an opponent's card effect" : Of(Causes, by);
        // A move "from" somewhere says it in the verb; anything else that happens there says where the card was.
        var moves = ev is "sent_gy" or "to_hand" or "to_deck" or "banished";
        if (!moves && from != "any" && !(ev == "leaves" && from == "field"))
        {
            if (Where(from) is not { } where) return string.Join(" ", words) + " " + Find(ev).Text + (from == "not_field" ? " " : " from ") + place;
            words.Add(where);
        }
        string verb;
        switch (ev)
        {
            case "sent_gy" when by is "destroy" or "destroy_battle_effect" or "destroy_effect" or "opponent_destroy" or "opponent_destroy_effect":
            {
                var how = by switch
                {
                    "destroy_battle_effect" => " by battle or card effect",
                    "destroy_effect" => " by a card effect",
                    "opponent_destroy" => " by an opponent's card",
                    "opponent_destroy_effect" => " by an opponent's card effect",
                    _ => "",
                };
                // "If this card on the field is destroyed by battle and sent to the GY".
                if (OnField(from)) words.Add(Where(from)!);
                verb = from == "any" || OnField(from) ? $"is destroyed{how} and sent to the GY" : $"is destroyed{how} and sent from {place} to the GY";
                cause = place = "";
                break;
            }
            case "sent_gy" when by is "discard" or "discard_effect" or "opponent_discard_effect" && from is "any" or "hand":
                verb = "is discarded";
                cause = by switch { "discard_effect" => "by a card effect", "opponent_discard_effect" => "by an opponent's card effect", _ => "" };
                place = "";
                break;
            case "sent_gy" when by == "reveal":
                verb = from == "any" ? "is excavated and sent to the GY" : $"is excavated from {place} and sent to the GY";
                cause = place = "";
                break;
            case "sent_gy" when from == "not_field":
                verb = "is sent to the GY, except from the field";
                place = "";
                break;
            case "sent_gy" when from == "overlay":
                verb = "is detached from an Xyz Monster and sent to the GY";
                place = "";
                break;
            case "sent_gy" when from != "any":
                verb = $"is sent from {place} to the GY";
                place = "";
                break;
            case "to_hand" when from == "deck":
                verb = "is added from the Deck to the hand";
                place = "";
                break;
            case "to_hand" when OnField(from):
                verb = $"is returned from {place} to the hand";
                place = "";
                break;
            // Ancient Gear Box: "If this card is added from the Deck or Graveyard to your hand, except by drawing it".
            case "to_hand" when from is "gy" or "deck_gy" or "hand_deck":
                verb = $"is added from {place} to your hand";
                place = "";
                break;
            case "to_deck" when now == "extra_faceup":
                verb = "is placed face-up in the Extra Deck";
                break;
            case "leaves":
                verb = "leaves the field";
                // "leaves the field because of an opponent's card effect".
                if (cause.StartsWith("by ", StringComparison.Ordinal) && by != "battle") cause = "because of " + cause[3..];
                place = "";
                break;
            case "material":
            {
                var kind = Materials.First(m => m.Key == args.Choice("material"));
                // "for the Link Summon of a WATER monster", as official cards say it.
                var summon = MaterialFor.Summon(kind.Label, args.Cards("summoner"), args.Pack);
                verb = now == "gy" ? $"is sent to the GY as material for {summon}" : $"is used as material for {summon}";
                break;
            }
            default:
                verb = Find(ev).Text;
                break;
        }
        if (moves && place.Length > 0) verb += (from == "not_field" ? " " : " from ") + place;
        if (cause.Length > 0 && !(ev == "battle_destroyed" && by == "battle")) verb += (IsException(cause) ? ", " : " ") + cause;
        if (now != "any" && !(ev == "material" && now == "gy") && !(ev == "to_deck" && now == "extra_faceup") && Of(Destinations, now) is { Length: > 0 } dest) verb += " " + dest;
        return string.Join(" ", words) + " " + verb;
    }

    private static string A(string noun) => (!noun.StartsWith('"') && "AEIOUaeiou".Contains(noun[0]) ? "an " : "a ") + noun;

    public override string Describe(BlockArgs args) => $"{(args.Choice("wording") == "when" ? "When" : "If")} {Event(args)}";

    public override EventPhrase? Phrase(BlockArgs args) => new(args.Choice("wording") == "when" ? "When" : "If", Event(args));

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var ev = Find(ctx.Args.Choice("event"));
        if (ev.Key is "battle_destroyed" or "material") Zones.RequireMonster(ctx, Name);
        var optional = ctx.Args.Bool("optional");
        e.Types.Add("EFFECT_TYPE_SINGLE");
        e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
        // "If" wording: the effect can't miss its timing.
        if (ctx.Args.Choice("wording") == "if") e.AddProperty("EFFECT_FLAG_DELAY");
        e.Code = ev.Code;
        e.IsMandatory = !optional;
        var tests = new List<string>();
        if (Places.First(p => p.Key == ctx.Args.Choice("from")).Lua is { } from)
            tests.Add(from.StartsWith("not:", StringComparison.Ordinal) ? $"not c:IsPreviousLocation({from[4..]})" : $"c:IsPreviousLocation({from})");
        switch (ctx.Args.Choice("face"))
        {
            case "up": tests.Add("c:IsPreviousPosition(POS_FACEUP)"); break;
            case "down": tests.Add("c:IsPreviousPosition(POS_FACEDOWN)"); break;
        }
        if (ctx.Args.Bool("yours")) tests.Add("c:IsPreviousControler(tp)");
        if (ctx.Args.Choice("summoned") is var how && how != "any")
            tests.Add($"c:IsSummonType(SUMMON_TYPE_{(how == "tribute" ? "ADVANCE" : how.ToUpperInvariant())})");
        if (Causes.First(c => c.Key == ctx.Args.Choice("by")).Lua is { } by) tests.Add(by);
        if (ev.Key == "material")
        {
            if (Materials.First(m => m.Key == ctx.Args.Choice("material")).Reason is { } reason) tests.Add($"r=={reason}");
            var summoner = ctx.Args.Cards("summoner").Clone();
            summoner.Zones = Zone.None;
            if (FilterCompiler.Conditions(summoner).Any(c => c != "c:IsType(TYPE_MONSTER)") || summoner.CardCode != 0)
                tests.Add($"{ctx.Script.Filter(summoner, null, "summonerfilter").Function}(c:GetReasonCard(),e,tp)");
        }
        if (Destinations.First(d => d.Key == ctx.Args.Choice("now")).Lua is { } now) tests.Add(now);
        if (ctx.Args.Bool("not_returned")) tests.Add("not c:IsReason(REASON_RETURN)");
        if (tests.Count == 0) return;
        var con = ctx.Script.AddFunction("selfcon", "e,tp,eg,ep,ev,re,r,rp", ["local c=e:GetHandler()", $"return {string.Join(" and ", tests)}"]);
        e.Conditions.Add($"{con}(e,tp,eg,ep,ev,re,r,rp)");
    }
}

/// <summary>
/// "If a "Blue-Eyes" monster(s) you control is destroyed by battle or card effect", "If another monster(s) is Special
/// Summoned from the Extra Deck to your opponent's field", "If a card(s) is banished from your GY", "If your opponent
/// Normal Summons a monster": matching cards that something happens to, with the details of where from and how.
/// </summary>
public sealed class CardsDetailTrigger : BlockDefinition
{
    public const string BlockId = "when.cards_detail";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Something happens";
    public override string Name => "Cards are Summoned, destroyed, sent, banished... (which, whose, where from, by what)";
    public override string Sentence => "{wording} {other} {count} {cards} {whose} {event} {summon_type} {summon_from} {from} {face} {by} {player} {now}, {zone} {damage_step} {speed} {already_gy}";
    public override string Help => "Every detail is optional. \"Whose\" is who controls them now, who controlled them before they moved, or who Summoned them.";

    /// <summary>One thing that can happen to cards: its event, card text, and whether \"whose\" is who controlled them before.</summary>
    public sealed record Happening(string Key, string Code, string Text, bool Before);

    public static readonly Happening[] Happenings =
    [
        new("special", "EVENT_SPSUMMON_SUCCESS", "Special Summoned", false),
        new("normal", "EVENT_SUMMON_SUCCESS", "Normal Summoned", false),
        new("flip", "EVENT_FLIP_SUMMON_SUCCESS", "Flip Summoned", false),
        new("destroyed", "EVENT_DESTROYED", "destroyed", true),
        new("battle_destroyed", "EVENT_BATTLE_DESTROYED", "destroyed by battle", true),
        new("sent_gy", "EVENT_TO_GRAVE", "sent to the GY", false),
        new("banished", "EVENT_REMOVE", "banished", false),
        new("tributed", "EVENT_RELEASE", "Tributed", true),
        new("to_hand", "EVENT_TO_HAND", "added to the hand", false),
        new("to_deck", "EVENT_TO_DECK", "returned to the Deck", false),
        new("leaves", "EVENT_LEAVE_FIELD", "leaves the field", true),
        new("discarded", "EVENT_DISCARD", "discarded", true),
        new("position", "EVENT_CHANGE_POS", "changed to another battle position", false),
        new("flipped", "EVENT_FLIP", "flipped face-up", false),
        // Sorcerer of Sebek: "When this card or your monster this card points to inflicts battle damage to your opponent".
        new("battle_damage", "EVENT_BATTLE_DAMAGE", "inflicts battle damage to your opponent", false),
    ];

    public static Happening Find(string key) => Happenings.FirstOrDefault(h => h.Key == key) ?? Happenings[0];

    private static bool IsSummon(string key) => key is "special" or "normal" or "flip";

    public static readonly (string Key, string Text, string? Lua)[] SummonFrom =
    [
        ("any", "", null), ("hand", "from the hand", "LOCATION_HAND"), ("deck", "from the Deck", "LOCATION_DECK"), ("gy", "from the GY", "LOCATION_GRAVE"),
        ("banished", "from banishment", "LOCATION_REMOVED"), ("extra", "from the Extra Deck", "LOCATION_EXTRA"), ("hand_gy", "from the hand or GY", "LOCATION_HAND+LOCATION_GRAVE"),
    ];

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec { Key = "cards", Label = "Which cards", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster } },
        ParamSpec.Choice("whose", "Whose", new("control", "", "who controls them (controlled, for cards that left the field)"), new("summoner", "", "who Summoned them"),
            new("now", "", "who controls them now"), new("before", "", "who controlled them before"), new("both", "", "who controls them now and controlled them before")),
        ParamSpec.Choice("event", "What happens to them", Happenings.Select(h => new ChoiceOption(h.Key, h.Text)).ToArray()),
        ParamSpec.Choice("summon_type", "Summoned how", new("any", "", "any way"), new("ritual", "Ritual"), new("fusion", "Fusion"), new("synchro", "Synchro"),
            new("xyz", "Xyz"), new("pendulum", "Pendulum"), new("link", "Link"), new("tribute", "Tribute")).ShowIf(b => IsSummon(b.Values.GetValueOrDefault("event") ?? "special")),
        ParamSpec.Choice("summon_from", "Summoned from", SummonFrom.Select(s => new ChoiceOption(s.Key, s.Text, s.Key == "any" ? "anywhere" : null)).ToArray())
            .ShowIf(b => IsSummon(b.Values.GetValueOrDefault("event") ?? "special")),
        ParamSpec.Choice("from", "Moved from", ThisCardDetailTrigger.Places.Select(p => new ChoiceOption(p.Key, p.Text, p.Key == "any" ? "anywhere" : p.Text)).ToArray()),
        ParamSpec.Choice("face", "They were", new("any", "", "face-up or face-down"), new("up", "face-up"), new("down", "face-down (Set)")),
        ParamSpec.Choice("by", "Because of", ThisCardDetailTrigger.Causes.Where(c => c.Key != "battle_or_opponent_effect_own").Select(c => new ChoiceOption(c.Key, c.Text, c.Key == "any" ? "anything" : null)).ToArray()),
        ParamSpec.Choice("player", "Caused by", new("any", "", "either player"), new("opponent", "by your opponent", "your opponent (their card or effect)"), new("you", "by you", "you (your card or effect)")),
        ParamSpec.Choice("now", "Now they are", ThisCardDetailTrigger.Destinations.Select(d => new ChoiceOption(d.Key, d.Text, d.Key == "any" ? "anywhere" : d.Text)).ToArray()),
        ParamSpec.Choice("other", "This card", new("any", "", "may be one of them"), new("other", "another", "one of them is another card"),
            new("not_this", "another", "this card isn't one of them"), new("other_not_this", "another", "another card, and this card isn't one of them")),
        ParamSpec.Choice("count", "How many", new("any", "", "any number"), new("one", "", "exactly 1 card"), new("two", "", "exactly 2 cards (at the same time)")),
        // A card that left the field may not be what it was there (an effect changed its Type, or it was face-down).
        ParamSpec.Flag("previous", "check what they were on the field (name, archetype, Type, Attribute, Level, ATK, DEF)", false)
            .ShowIf(b => !IsSummon(b.Values.GetValueOrDefault("event") ?? "special")),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices),
        ParamSpec.Choice("wording", "Wording", new("if", "If", "If ... (can't miss its timing)"), new("when", "When", "When ... (can miss its timing)")),
        // Battle destruction happens in the Damage Step, where optional triggers from the field need leave to activate.
        ParamSpec.Choice("damage_step", "In the Damage Step",
            new("no", "", "can't be used in the Damage Step"), new("step", "", "can also be used in the Damage Step"),
            new("yes", "", "can also be used in the Damage Step, damage calculation included")),
        ParamSpec.Flag("optional", "optional (\"You can\")", true),
        // Droll & Lock Bird: "If a card(s) is added from the Main Deck to your opponent's hand, except during the Draw Phase (Quick Effect)".
        ParamSpec.Choice("speed", "Activated as", new("trigger", "", "a trigger effect"), new("quick", "", "a Quick Effect, right as it happens")),
        // Infinitrack Road Roller: "from the GY (if it was there when the monster was Tributed or banished)".
        ParamSpec.Flag("already_gy", "this card was already in the GY when it happened (if it is in the GY)", false),
    ];

    /// <summary>
    /// What a card was on the field, as the engine remembers it after it left: its name, archetype, Type, Attribute,
    /// Level, ATK and DEF there. The picker's other details are checked as the card is now.
    /// </summary>
    public static List<string> PreviousConditions(CardFilter f)
    {
        var parts = new List<string>();
        var now = f.Clone();
        now.CardType = FilterCardType.Any;
        now.MonsterKind = MonsterKind.Any;
        now.CardCode = 0;
        now.AltCodes = [];
        now.Archetype = now.Archetype2 = 0;
        now.Race = CardRace.None;
        now.Attribute = CardAttribute.None;
        now.LevelMin = now.LevelMax = now.AtkMin = now.AtkMax = now.DefMin = now.DefMax = null;
        string Type(string t) => $"c:GetPreviousTypeOnField()&{t}~=0";
        switch (f.CardType)
        {
            case FilterCardType.Monster: parts.Add(Type("TYPE_MONSTER")); break;
            case FilterCardType.Spell: parts.Add(Type("TYPE_SPELL")); break;
            case FilterCardType.Trap: parts.Add(Type("TYPE_TRAP")); break;
            case FilterCardType.SpellTrap: parts.Add(Type("(TYPE_SPELL+TYPE_TRAP)")); break;
        }
        if (FilterCompiler.KindType(f.MonsterKind) is { } kind) parts.Add(kind.Not ? $"c:GetPreviousTypeOnField()&{kind.Type}==0" : Type(kind.Type));
        if (f.CardCode != 0)
            parts.Add("(" + string.Join(" or ", new[] { f.CardCode }.Concat(f.AltCodes).Select(code => $"c:GetPreviousCodeOnField()=={Lua.Num(code)}")) + ")");
        if (f.Archetype != 0 && f.Archetype2 != 0) parts.Add($"(c:IsPreviousSetCard({Lua.Hex(f.Archetype)}) or c:IsPreviousSetCard({Lua.Hex(f.Archetype2)}))");
        else if (f.Archetype != 0) parts.Add($"c:IsPreviousSetCard({Lua.Hex(f.Archetype)})");
        if (f.Race != CardRace.None) parts.Add($"c:GetPreviousRaceOnField()&{EnumNames.LuaConstant(f.Race)}~=0");
        if (f.Attribute != CardAttribute.None) parts.Add($"c:GetPreviousAttributeOnField()&{EnumNames.LuaConstant(f.Attribute)}~=0");
        if (f.LevelMin is { } lmin) parts.Add($"c:GetPreviousLevelOnField()>={lmin}");
        if (f.LevelMax is { } lmax) parts.Add($"c:GetPreviousLevelOnField()<={lmax}");
        if (f.AtkMin is { } amin) parts.Add($"c:GetPreviousAttackOnField()>={amin}");
        if (f.AtkMax is { } amax) parts.Add($"c:GetPreviousAttackOnField()<={amax}");
        if (f.DefMin is { } dmin) parts.Add($"c:GetPreviousDefenseOnField()>={dmin}");
        if (f.DefMax is { } dmax) parts.Add($"c:GetPreviousDefenseOnField()<={dmax}");
        parts.AddRange(FilterCompiler.Conditions(now));
        return parts;
    }

    /// <summary>"a "Blue-Eyes" monster(s) you control is destroyed by battle", "your opponent Special Summons a monster(s) from the Extra Deck".</summary>
    /// <param name="kindSlot">For a Summon, what to write in place of its kind ("{0}" for combining).</param>
    private static string Event(BlockArgs args, string? kindSlot = null)
    {
        var ev = Find(args.Choice("event"));
        var f = args.Cards("cards");
        var whose = args.Choice("whose");
        // Card text says "another" both when one of them is another card and when this card isn't one of them.
        var other = args.Choice("other") is not "any";
        var one = args.Choice("count") == "one";
        var two = args.Choice("count") == "two";
        var noun = FilterText.Describe(f, args.Pack, false, "", withLocation: false, withExcept: false);
        if (noun.StartsWith("other ", StringComparison.Ordinal)) noun = noun[6..];
        // "your monster this card points to inflicts battle damage to your opponent".
        if (ev.Key == "battle_damage") return $"{BattleSubject(f, noun)} inflicts battle damage to your opponent{Tail(args)}";
        if (two)
        {
            var many = "2 " + FilterText.Describe(f, args.Pack, true, "", withLocation: false, withExcept: false);
            if (many.StartsWith("2 other ", StringComparison.Ordinal)) many = "2 " + many[8..];
            if (args.Choice("face") is var face2 && face2 != "any") many = "2 " + (face2 == "up" ? "face-up " : "Set ") + many[2..];
            noun = many;
        }
        // "a face-up "Raidraptor" card(s)", "a Set card(s)": how they were before they moved.
        if (args.Choice("face") is var face && face != "any" && !noun.StartsWith("face-", StringComparison.Ordinal))
            noun = (face == "up" ? "face-up " : "Set ") + noun;
        if (!two)
        {
            noun = one ? "exactly 1 " + noun : (other ? "another " : (!noun.StartsWith('"') && "AEIOUaeiou".Contains(noun[0]) ? "an " : "a ")) + noun + "(s)";
            if (one && other) noun = "exactly 1 other " + noun["exactly 1 ".Length..];
        }
        // "2 monsters are Special Summoned at the same time": both in one event.
        var be = two ? "are" : "is";
        var together = two ? " at the same time" : "";
        var summonFrom = SummonFrom.First(s => s.Key == args.Choice("summon_from")).Text;
        var tail = Tail(args);
        if (IsSummon(ev.Key))
        {
            var kind = kindSlot ?? SummonKind(args);
            var sf = summonFrom.Length > 0 ? " " + summonFrom : "";
            if (whose == "summoner" && f.Owner != CardOwner.Either)
            {
                var obj = noun;
                return f.Owner == CardOwner.You ? $"you {kind} Summon {obj}{together}{sf}{tail}" : $"your opponent {kind} Summons {obj}{together}{sf}{tail}";
            }
            // Qliphort Genius: "Special Summoned at the same time to the zones this card points to".
            var to = f.PointedToByThisCard ? " to the zones this card points to"
                : f.Owner switch { CardOwner.You => " to your field", CardOwner.Opponent => " to your opponent's field", _ => "" };
            return $"{noun} {be} {kind} Summoned{together}{sf}{to}{tail}";
        }
        string owner = !SaysControl(args) ? "" : f.Owner == CardOwner.You ? " you control" : " your opponent controls";
        var gy = whose != "before" && f.Owner != CardOwner.Either ? (f.Owner == CardOwner.You ? "your GY" : "your opponent's GY") : "the GY";
        var verb = ev.Key switch
        {
            "sent_gy" when DestroyedAndSent(args) is { } how => $"is destroyed{how} and sent to {gy}",
            "sent_gy" => $"is sent to {gy}",
            // Droll & Lock Bird: "is added from the Main Deck to your opponent's hand".
            "to_hand" when FromSaidInVerb(args) => $"is added from the Main Deck to {(f.Owner == CardOwner.You ? "your" : "your opponent's")} hand",
            "to_hand" when whose != "before" && f.Owner != CardOwner.Either => $"is added to {(f.Owner == CardOwner.You ? "your" : "your opponent's")} hand",
            "leaves" => "leaves the field",
            _ => "is " + ev.Text,
        };
        if (two) verb = (verb.StartsWith("is ", StringComparison.Ordinal) ? "are " + verb[3..] : verb == "leaves the field" ? "leave the field" : verb) + together;
        return $"{noun}{owner} {verb}{tail}";
    }

    /// <summary>"your monster this card points to", "a monster": who inflicts the battle damage.</summary>
    private static string BattleSubject(CardFilter f, string noun)
    {
        var subject = f.Owner switch
        {
            CardOwner.You => "your " + noun,
            CardOwner.Opponent => "your opponent's " + noun,
            _ => (!noun.StartsWith('"') && "AEIOUaeiou".Contains(noun[0]) ? "an " : "a ") + noun,
        };
        return f.PointedToByThisCard ? subject + " this card points to" : subject;
    }

    /// <summary>Cards added from the Deck to a player's hand: the verb says where from.</summary>
    private static bool FromSaidInVerb(BlockArgs args) => args.Choice("event") == "to_hand" && args.Choice("from") == "deck"
        && args.Choice("whose") != "before" && args.Cards("cards").Owner != CardOwner.Either;

    /// <summary>Whether the card text says "you control" or "your opponent controls" after the cards.</summary>
    private static bool SaysControl(BlockArgs args)
    {
        var ev = args.Choice("event");
        if (args.Cards("cards").Owner == CardOwner.Either || OwnedPlace(args) is not null || IsSummon(ev)) return false;
        // "is sent to your GY", "is added to your hand": where they go says whose they are.
        return args.Choice("whose") == "before" || ev is not ("sent_gy" or "to_hand");
    }

    /// <summary>" by a card effect" for cards sent to the GY because they were destroyed: "is destroyed by a card effect and sent to the GY".</summary>
    private static string? DestroyedAndSent(BlockArgs args) => args.Choice("event") != "sent_gy" ? null : args.Choice("by") switch
    {
        "destroy" => "",
        "destroy_battle_effect" => " by battle or card effect",
        "destroy_effect" => " by a card effect",
        "opponent_destroy" => " by an opponent's card",
        "opponent_destroy_effect" => " by an opponent's card effect",
        _ => null,
    };

    /// <summary>"Normal", "Special", "Synchro": the Summon's kind as the card text names it.</summary>
    private static string SummonKind(BlockArgs args)
    {
        var how = args.Choice("summon_type") is var st && st != "any" ? args.Describe("summon_type") : "";
        if (how.Length > 0) return how;
        return args.Choice("event") switch { "normal" => "Normal", "flip" => "Flip", _ => "Special" };
    }

    /// <summary>
    /// "your opponent's Deck": where the cards were, for cards their controller before had there, which reads better
    /// than "a card your opponent controls is sent from the Deck".
    /// </summary>
    private static string? OwnedPlace(BlockArgs args)
    {
        var owner = args.Cards("cards").Owner;
        if (owner == CardOwner.Either || args.Choice("whose") != "before" || IsSummon(args.Choice("event"))) return null;
        var place = args.Choice("from") switch
        {
            "hand" => "hand", "deck" => "Deck", "extra" => "Extra Deck", "gy" => "GY", "hand_deck" => "hand or Deck", _ => null,
        };
        return place is null ? null : (owner == CardOwner.You ? "your " : "your opponent's ") + place;
    }

    /// <summary>", from the GY, by a card effect": the details after the event.</summary>
    private static string Tail(BlockArgs args)
    {
        var parts = new List<string>();
        var from = args.Choice("from");
        // "a monster you control is destroyed": the field goes without saying.
        var said = SaysControl(args) && from is "field" or "mzone";
        if (OwnedPlace(args) is { } owned) parts.Add("from " + owned);
        else if (from != "any" && !said && !FromSaidInVerb(args)) parts.Add((from == "not_field" ? "" : "from ") + ThisCardDetailTrigger.Places.First(p => p.Key == from).Text);
        var by = args.Choice("by");
        if (by != "any" && DestroyedAndSent(args) is null) parts.Add(ThisCardDetailTrigger.Causes.First(c => c.Key == by).Text);
        if (args.Choice("player") != "any") parts.Add(args.Describe("player"));
        var now = args.Choice("now");
        if (now != "any" && ThisCardDetailTrigger.Destinations.First(d => d.Key == now).Text is { Length: > 0 } dest) parts.Add(dest);
        // "is sent to the GY, except by battle": an exception goes after a comma.
        return string.Concat(parts.Select(p => (ThisCardDetailTrigger.IsException(p) ? ", " : " ") + p));
    }

    public override string Describe(BlockArgs args) => $"{(args.Choice("wording") == "when" ? "When" : "If")} {Event(args)}";

    // Summons combine on their kind: "If you Normal or Special Summon a monster(s)". Battle damage combines on who
    // inflicts it: "When this card or your monster this card points to inflicts battle damage to your opponent".
    public override EventPhrase? Phrase(BlockArgs args)
    {
        var lead = args.Choice("wording") == "when" ? "When" : "If";
        if (args.Choice("event") == "battle_damage" && Tail(args).Length == 0)
        {
            var noun = FilterText.Describe(args.Cards("cards"), args.Pack, false, "", withLocation: false, withExcept: false);
            return new(lead, ThisCardEventTrigger.BattleDamageTemplate, BattleSubject(args.Cards("cards"), noun), Event(args));
        }
        return IsSummon(args.Choice("event")) ? new(lead, Event(args, "{0}"), SummonKind(args), Event(args)) : new(lead, Event(args));
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var ev = Find(ctx.Args.Choice("event"));
        var optional = ctx.Args.Bool("optional");
        if (ctx.Args.Choice("speed") == "quick")
        {
            // A Quick Effect that responds to the event itself, like Droll & Lock Bird's.
            e.Types.Add("EFFECT_TYPE_QUICK_O");
            if (!optional) e.Warn("A Quick Effect is always optional (\"You can\"); \"optional\" was ignored.");
        }
        else
        {
            e.Types.Add("EFFECT_TYPE_FIELD");
            e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
            e.IsMandatory = !optional;
        }
        if (ctx.Args.Choice("wording") == "if") e.AddProperty("EFFECT_FLAG_DELAY");
        e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
        switch (ctx.Args.Choice("damage_step"))
        {
            case "yes": e.AddProperty("EFFECT_FLAG_DAMAGE_STEP"); e.AddProperty("EFFECT_FLAG_DAMAGE_CAL"); break;
            case "step": e.AddProperty("EFFECT_FLAG_DAMAGE_STEP"); break;
        }
        e.Code = ev.Code;
        var f = ctx.Args.Cards("cards").Clone();
        f.Zones = Zone.None;
        f.ExcludeThisCard = false;
        var parts = ctx.Args.Bool("previous") && !IsSummon(ev.Key) ? PreviousConditions(f) : FilterCompiler.Conditions(f);
        var whose = ctx.Args.Choice("whose") switch
        {
            "summoner" => "IsSummonPlayer",
            "now" => "IsControler",
            "before" => "IsPreviousControler",
            "both" => "both",
            _ => ev.Before ? "IsPreviousControler" : "IsControler",
        };
        if (f.Owner != CardOwner.Either)
        {
            var player = f.Owner == CardOwner.You ? "tp" : "1-tp";
            parts.Add(whose == "both" ? $"c:IsControler({player}) and c:IsPreviousControler({player})" : $"c:{whose}({player})");
        }
        var type = ctx.Args.Choice("summon_type");
        if (IsSummon(ev.Key) && type != "any") parts.Add($"c:IsSummonType(SUMMON_TYPE_{(type == "tribute" ? "ADVANCE" : type.ToUpperInvariant())})");
        if (IsSummon(ev.Key) && SummonFrom.First(s => s.Key == ctx.Args.Choice("summon_from")).Lua is { } sf) parts.Add($"c:IsSummonLocation({sf})");
        if (ThisCardDetailTrigger.Places.First(p => p.Key == ctx.Args.Choice("from")).Lua is { } from)
            parts.Add(from.StartsWith("not:", StringComparison.Ordinal) ? $"not c:IsPreviousLocation({from[4..]})" : $"c:IsPreviousLocation({from})");
        switch (ctx.Args.Choice("face"))
        {
            case "up": parts.Add("c:IsPreviousPosition(POS_FACEUP)"); break;
            case "down": parts.Add("c:IsPreviousPosition(POS_FACEDOWN)"); break;
        }
        if (ThisCardDetailTrigger.Causes.First(c => c.Key == ctx.Args.Choice("by")).Lua is { } by) parts.Add(by);
        if (ThisCardDetailTrigger.Destinations.First(d => d.Key == ctx.Args.Choice("now")).Lua is { } now) parts.Add(now);
        if (ctx.Args.Bool("already_gy"))
        {
            // Not by the effect that sent this card to the GY at the same time (aux.AddThisCardInGraveAlreadyCheck).
            ctx.Script.AddInitialLine("s.cc_ingy=s.cc_ingy or {}\n\ts.cc_ingy[c]=aux.AddThisCardInGraveAlreadyCheck(c)");
            parts.Add("(s.cc_ingy[e:GetHandler()]:GetLabelObject()==nil or c:GetReasonEffect()~=s.cc_ingy[e:GetHandler()]:GetLabelObject())");
        }
        var other = ctx.Args.Choice("other");
        var fn = ctx.Script.AddFunction("evcards", "c,e,tp", [$"return {(parts.Count == 0 ? "true" : string.Join(" and ", parts))}"]);
        var exclude = other is "other" or "other_not_this" ? "e:GetHandler()" : "nil";
        var tests = new List<string> { ctx.Args.Choice("count") == "two" ? $"eg:FilterCount({fn},{exclude},e,tp)==2" : $"eg:IsExists({fn},1,{exclude},e,tp)" };
        if (other is "not_this" or "other_not_this") tests.Add("not eg:IsContains(e:GetHandler())");
        if (ctx.Args.Choice("count") == "one") tests.Add("eg:GetCount()==1");
        if (ev.Key == "battle_damage") tests.Add("ep~=tp");
        switch (ctx.Args.Choice("player"))
        {
            case "opponent": tests.Add("rp==1-tp"); break;
            case "you": tests.Add("rp==tp"); break;
        }
        var con = ctx.Script.AddFunction("evcon", "e,tp,eg,ep,ev,re,r,rp", [$"return {string.Join(" and ", tests)}"]);
        e.Conditions.Add($"{con}(e,tp,eg,ep,ev,re,r,rp)");
    }
}
