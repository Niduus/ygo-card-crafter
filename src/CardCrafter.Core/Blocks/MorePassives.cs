using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Lasting effects and conditions added from the scan of every official card script.

public sealed class ImmunePassive : PassiveBlock
{
    public override string Id => "passive.immune";
    public override string Group => "This card";
    public override string Name => "This card (or chosen monsters) is unaffected by effects";
    public override string Sentence => "{cards} is unaffected by {whose} {attribute} {except}";
    public override string Help => "Pick monsters instead of this card for a lasting effect on them, like Salamangreat Shrine: \"\"Salamangreat\" monsters you " +
                                   "control are unaffected by your opponent's activated FIRE monsters' effects\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        // Saved before monsters could be picked: this card.
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { ThisCard = true }, Zone.MonsterZone, thisCard: true),
        ParamSpec.Choice("whose", "By",
            new("opponent", "your opponent's card effects"),
            new("other", "other cards' effects"),
            new("spelltrap", "Spell/Trap effects"),
            new("monster", "other monsters' effects"),
            new("opp_monster", "your opponent's monster effects"),
            new("spell", "Spell effects"),
            new("trap", "Trap effects"),
            new("opp_spell", "your opponent's Spell effects"),
            new("opp_trap", "your opponent's Trap effects"),
            new("opp_activated", "your opponent's activated effects"),
            new("opp_activated_monster", "your opponent's activated monster effects"),
            new("other_activated", "other cards' activated effects"),
            new("opp_spelltrap", "your opponent's Spell/Trap effects"),
            // Abyss Vagrada, Divine Anima Infinity.
            new("less_atk", "the activated effects of monsters with ATK less than this card"),
            // Omega Vidolium: "Unaffected by card effects, except "Power Patron" cards'".
            new("except_cards", "card effects, except", "card effects, except those of the cards picked below"),
            // Qliphort Genius.
            new("other_link", "the activated effects of other Link Monsters"),
            new("spelltrap_other_link", "Spell/Trap effects and the activated effects of other Link Monsters"),
            // Salamangreat Shrine: "your opponent's activated FIRE monsters' effects".
            new("opp_activated_attr", "your opponent's activated", "your opponent's activated monster effects of an Attribute:")),
        new ParamSpec { Key = "attribute", Label = "Attribute", Type = ParamType.Choice, Default = MonsterChoices.Attributes[0].Value, Choices = MonsterChoices.Attributes }
            .Unprinted().ShowIf(b => b.Values.GetValueOrDefault("whose") == "opp_activated_attr"),
        ParamSpec.Cards("except", "Except the effects of", () => new CardFilter { Zones = Zone.None }).Unprinted().WhenUsed()
            .ShowIf(b => b.Values.GetValueOrDefault("whose") == "except_cards"),
    ];

    /// <summary>"your opponent's activated FIRE monsters' effects", "card effects, except "Power Patron" cards'".</summary>
    private static string Whose(BlockArgs args) => args.Choice("whose") switch
    {
        "opp_activated_attr" => $"your opponent's activated {MonsterChoices.Attributes.First(a => a.Value == args.Choice("attribute")).Label} monsters' effects",
        "except_cards" => $"card effects, except {FilterText.Describe(args.Cards("except"), args.Pack, plural: true, withLocation: false, ownName: args.CardName)}'",
        _ => args.Describe("whose"),
    };

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        if (f.ThisCard) return $"this card is unaffected by {Whose(args)}";
        return $"{FilterText.Describe(f, args.Pack, plural: true, preposition: "", ownName: args.CardName)} are unaffected by {Whose(args)}";
    }

    public override void Compile(BlockContext ctx)
    {
        var test = ctx.Args.Choice("whose") switch
        {
            "less_atk" => "te:IsActivated() and te:IsActiveType(TYPE_MONSTER) and te:GetHandler():GetAttack()<e:GetHandler():GetAttack()",
            "except_cards" => ctx.Script.MaterialFilter(ctx.Args.Cards("except"), "immexfilter") is var ex && ex != "nil" ? $"not {ex}(te:GetHandler())" : "true",
            "other_link" => "te:IsActiveType(TYPE_LINK) and te:IsActivated() and te:GetOwner()~=e:GetOwner()",
            "spelltrap_other_link" => "te:IsActiveType(TYPE_SPELL+TYPE_TRAP) or te:IsActiveType(TYPE_LINK) and te:IsActivated() and te:GetOwner()~=e:GetOwner()",
            "opp_activated_attr" => "te:GetOwnerPlayer()~=e:GetHandlerPlayer() and te:IsActivated() and te:IsActiveType(TYPE_MONSTER) and te:GetHandler():IsAttribute("
                                    + EnumNames.LuaConstant(Enum.Parse<CardAttribute>(ctx.Args.Choice("attribute"))) + ")",
            "other" => "te:GetOwner()~=e:GetOwner()",
            "spelltrap" => "te:IsActiveType(TYPE_SPELL+TYPE_TRAP)",
            "monster" => "te:IsActiveType(TYPE_MONSTER) and te:GetOwner()~=e:GetOwner()",
            "opp_monster" => "te:IsActiveType(TYPE_MONSTER) and te:GetOwnerPlayer()~=e:GetHandlerPlayer()",
            "spell" => "te:IsActiveType(TYPE_SPELL)",
            "trap" => "te:IsActiveType(TYPE_TRAP)",
            "opp_spell" => "te:IsActiveType(TYPE_SPELL) and te:GetOwnerPlayer()~=e:GetHandlerPlayer()",
            "opp_trap" => "te:IsActiveType(TYPE_TRAP) and te:GetOwnerPlayer()~=e:GetHandlerPlayer()",
            "opp_activated" => "te:GetOwnerPlayer()~=e:GetHandlerPlayer() and te:IsActivated()",
            "opp_activated_monster" => "te:GetOwnerPlayer()~=e:GetHandlerPlayer() and te:IsActivated() and te:IsActiveType(TYPE_MONSTER)",
            "other_activated" => "te:GetOwner()~=e:GetOwner() and te:IsActivated()",
            "opp_spelltrap" => "te:IsActiveType(TYPE_SPELL+TYPE_TRAP) and te:GetOwnerPlayer()~=e:GetHandlerPlayer()",
            _ => "te:GetOwnerPlayer()~=e:GetHandlerPlayer()",
        };
        var fn = ctx.Script.AddFunction("immval", "e,te", [$"return {test}"]);
        var range = RangeOf(ctx);
        var filter = ctx.Args.Cards("cards").Clone();
        if (filter.ThisCard)
        {
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_IMMUNE_EFFECT", fn));
            return;
        }
        // The monsters the picker allows, as long as this card applies it.
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("immtg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode(EFFECT_IMMUNE_EFFECT)", $"{v}:SetRange({range})",
            $"{v}:SetTargetRange({f.Self},{f.Opp})", $"{v}:SetTarget({target})", $"{v}:SetValue({fn})",
        ]);
    }
}

/// <summary>Rules for this card: can't attack, can't be attacked, can't be Tributed, no battle damage...</summary>
public sealed class SelfRulePassive : PassiveBlock
{
    public override string Id => "passive.self_rule";
    public override string Group => "This card";
    public override string Name => "This card can't attack / be attacked / be Tributed...";
    public override string Sentence => "{rule}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("rule", "Rule",
            new("cannot_attack", "This card cannot attack"),
            new("cannot_direct", "This card cannot attack directly"),
            new("not_attack_target", "Your opponent cannot target this card for attacks"),
            new("no_battle_damage", "You take no battle damage from battles involving this card"),
            new("cannot_tribute", "This card cannot be Tributed"),
            new("cannot_material", "This card cannot be used as material for a Fusion, Synchro, Xyz or Link Summon"),
            new("cannot_change_position", "This card's battle position cannot be changed"),
            new("attack_all", "This card can attack all monsters your opponent controls once each"),
            new("must_attack", "This card must attack, if able"),
            new("battle_safe", "If this card battles a monster, neither can be destroyed by that battle"),
            new("opp_no_battle_damage", "Your opponent takes no battle damage from attacks involving this card"),
            new("reflect_battle", "Your opponent takes any battle damage you would have taken from battles involving this card"),
            // Heart of the Blue-Eyes.
            new("control_fixed", "Control of this card cannot switch")),
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var range = RangeOf(ctx);
        if (ctx.Args.Choice("rule") == "battle_safe")
        {
            // Dark-Eyes Illusionist Faceless Mage: this card and the monster it battles both survive the battle.
            var tg = ctx.Script.AddFunction("indtg", "e,c", ["local h=e:GetHandler()", "return c==h or c==h:GetBattleTarget()"]);
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode(EFFECT_INDESTRUCTABLE_BATTLE)", $"{v}:SetRange({range})",
                $"{v}:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)", $"{v}:SetTarget({tg})", $"{v}:SetValue(1)"]);
            return;
        }
        var codes = ctx.Args.Choice("rule") switch
        {
            "cannot_direct" => new[] { ("EFFECT_CANNOT_DIRECT_ATTACK", "1") },
            "not_attack_target" => [("EFFECT_CANNOT_BE_BATTLE_TARGET", "aux.imval1")],
            "no_battle_damage" => [("EFFECT_AVOID_BATTLE_DAMAGE", "1")],
            "cannot_tribute" => [("EFFECT_UNRELEASABLE_SUM", "1"), ("EFFECT_UNRELEASABLE_NONSUM", "1")],
            "cannot_material" => [("EFFECT_CANNOT_BE_FUSION_MATERIAL", "1"), ("EFFECT_CANNOT_BE_SYNCHRO_MATERIAL", "1"), ("EFFECT_CANNOT_BE_XYZ_MATERIAL", "1"), ("EFFECT_CANNOT_BE_LINK_MATERIAL", "1")],
            "cannot_change_position" => [("EFFECT_CANNOT_CHANGE_POSITION", "1")],
            "attack_all" => [("EFFECT_ATTACK_ALL", "1")],
            "must_attack" => [("EFFECT_MUST_ATTACK", "1")],
            "opp_no_battle_damage" => [("EFFECT_NO_BATTLE_DAMAGE", "1")],
            "reflect_battle" => [("EFFECT_REFLECT_BATTLE_DAMAGE", "1")],
            "control_fixed" => [("EFFECT_CANNOT_CHANGE_CONTROL", "1")],
            _ => [("EFFECT_CANNOT_ATTACK", "1")],
        };
        foreach (var (code, value) in codes) ctx.Effect.AddPassive(v => SelfEffect(v, range, code, value));
    }
}

/// <summary>Rules for chosen monsters: they can't attack, their effects are negated, they can't be targeted...</summary>
public sealed class CardsRulePassive : PassiveBlock
{
    public override string Id => "passive.cards_rule";
    public override string Group => "Monsters";
    public override string Name => "Monsters can't attack / are negated / can't be targeted...";
    public override string Sentence => "{cards} {rule}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone, thisCard: true),
        ParamSpec.Choice("rule", "Rule",
            new("cannot_attack", "cannot attack"),
            new("negated", "have their effects negated"),
            new("untargetable", "cannot be targeted by your opponent's card effects"),
            new("untargetable_any", "cannot be targeted by card effects"),
            new("not_attack_target", "cannot be targeted for attacks"),
            new("unaffected", "are unaffected by your opponent's card effects"),
            new("pierce", "inflict piercing battle damage"),
            new("must_attack", "must attack, if able"),
            new("direct", "can attack directly"),
            new("cannot_direct", "cannot attack directly"),
            // Lethargisaurus, Infinitrack Road Roller: "Face-up monsters your opponent controls are changed to Defense Position".
            new("to_defense", "are changed to Defense Position")),
    ];

    public override string Describe(BlockArgs args)
    {
        if (args.Cards("cards").ThisCard)
        {
            // "this card has its effects negated", "this card is unaffected by ...", "this card inflicts piercing battle damage".
            var rule = args.Choice("rule") switch
            {
                "negated" => "has its effects negated",
                "unaffected" => "is unaffected by your opponent's card effects",
                "pierce" => "inflicts piercing battle damage",
                _ => args.Describe("rule"),
            };
            return "this card " + rule;
        }
        return $"{FilterText.Describe(args.Cards("cards"), args.Pack, plural: true, preposition: "")} {args.Describe("rule")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        if (filter.ThisCard)
        {
            var (selfCodes, selfValue) = ctx.Args.Choice("rule") switch
            {
                "negated" => (new[] { "EFFECT_DISABLE", "EFFECT_DISABLE_EFFECT" }, "1"),
                "untargetable" => (["EFFECT_CANNOT_BE_EFFECT_TARGET"], "aux.tgoval"),
                "untargetable_any" => (["EFFECT_CANNOT_BE_EFFECT_TARGET"], "1"),
                "not_attack_target" => (["EFFECT_CANNOT_BE_BATTLE_TARGET"], "aux.imval1"),
                "unaffected" => (["EFFECT_IMMUNE_EFFECT"], ctx.Script.AddFunction("immval", "e,te", ["return te:GetOwnerPlayer()~=e:GetHandlerPlayer()"])),
                "pierce" => (["EFFECT_PIERCE"], "1"),
                "must_attack" => (["EFFECT_MUST_ATTACK"], "1"),
                "direct" => (["EFFECT_DIRECT_ATTACK"], "1"),
                "cannot_direct" => (["EFFECT_CANNOT_DIRECT_ATTACK"], "1"),
                "to_defense" => (["EFFECT_SET_POSITION"], "POS_FACEUP_DEFENSE"),
                _ => (["EFFECT_CANNOT_ATTACK"], "1"),
            };
            OnThisCard(ctx, selfCodes, selfValue);
            return;
        }
        filter.Zones = Zone.MonsterZone;
        // Only face-up monsters change position (face-down ones stay Set), as official cards target them.
        if (ctx.Args.Choice("rule") == "to_defense") filter.FaceUpOnly = true;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("ruletg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        var range = RangeOf(ctx);
        var (codes, value) = ctx.Args.Choice("rule") switch
        {
            "negated" => (new[] { "EFFECT_DISABLE", "EFFECT_DISABLE_EFFECT" }, "1"),
            "untargetable" => (["EFFECT_CANNOT_BE_EFFECT_TARGET"], "aux.tgoval"),
            "untargetable_any" => (["EFFECT_CANNOT_BE_EFFECT_TARGET"], "1"),
            "not_attack_target" => (["EFFECT_CANNOT_BE_BATTLE_TARGET"], "aux.imval1"),
            "unaffected" => (["EFFECT_IMMUNE_EFFECT"], ctx.Script.AddFunction("immval", "e,te", ["return te:GetOwnerPlayer()~=e:GetHandlerPlayer()"])),
            "pierce" => (["EFFECT_PIERCE"], "1"),
            "must_attack" => (["EFFECT_MUST_ATTACK"], "1"),
            "direct" => (["EFFECT_DIRECT_ATTACK"], "1"),
            "cannot_direct" => (["EFFECT_CANNOT_DIRECT_ATTACK"], "1"),
            "to_defense" => (["EFFECT_SET_POSITION"], "POS_FACEUP_DEFENSE"),
            _ => (["EFFECT_CANNOT_ATTACK"], "1"),
        };
        foreach (var code in codes)
            ctx.Effect.AddPassive(v =>
            [
                $"{v}:SetType(EFFECT_TYPE_FIELD)",
                $"{v}:SetCode({code})",
                $"{v}:SetRange({range})",
                $"{v}:SetTargetRange({f.Self},{f.Opp})",
                $"{v}:SetTarget({target})",
                $"{v}:SetValue({value})",
            ]);
    }
}

/// <summary>Rules for a player: no battle damage, no effect damage, can't Special Summon from the Extra Deck...</summary>
public sealed class PlayerRulePassive : PassiveBlock
{
    public override string Id => "passive.player_rule";
    public override string Group => "Players";
    public override string Name => "You take no damage / your opponent can't Special Summon...";
    public override string Sentence => "{rule} {whose} {count}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("rule", "Rule",
            new("no_battle_damage", "You take no battle damage"),
            new("no_effect_damage", "You take no effect damage"),
            new("no_damage", "You take no damage"),
            new("half_damage", "Any damage you take is halved"),
            new("reflect_effect", "Effect damage you would take is inflicted to your opponent instead"),
            new("opp_double_battle", "Battle damage your opponent takes is doubled"),
            new("opp_no_effect_damage", "Your opponent takes no effect damage"),
            new("opp_no_extra", "Your opponent cannot Special Summon monsters from the Extra Deck"),
            new("opp_no_special", "Your opponent cannot Special Summon monsters"),
            new("opp_no_draw_extra", "Your opponent cannot add cards from their Deck to their hand except by drawing them"),
            new("opp_no_bp_activate", "Your opponent cannot activate cards or effects during the Battle Phase"),
            // Lethargisaurus.
            new("no_own_response", "Neither player can activate cards or effects in response to the activation of their own cards or effects"),
            // Invoked Caliga; Angelechy Shatranga: "your opponent can only attempt to activate up to 5 monster effects per turn".
            new("one_monster_effect", "Each player can only attempt to activate 1 monster effect per turn", "Each player can only attempt to activate N monster effects per turn (whose and how many below)"),
            new("one_attacker", "Each player can only attack with 1 monster during each Battle Phase"),
            // Angelechy Castellan.
            new("one_per_chain", "Each player can only activate 1 card or effect per Chain")),
        // Saved before these were offered: each player, 1 monster effect.
        ParamSpec.Choice("whose", "Whose (monster effects per turn)", new("each", "Each player"), new("opponent", "Your opponent"), new("you", "You"))
            .ShowIf(b => b.Values.GetValueOrDefault("rule") == "one_monster_effect").Unprinted(),
        ParamSpec.Int("count", "How many monster effects per turn", 1, 1, 10).ShowIf(b => b.Values.GetValueOrDefault("rule") == "one_monster_effect").Unprinted(),
    ];

    /// <summary>"each player can only ...": lowercase, so it reads right after "While this card is treated as a Continuous Spell, ".</summary>
    public override string Describe(BlockArgs args)
    {
        var text = args.Describe("rule");
        if (args.Choice("rule") == "one_monster_effect")
        {
            var n = args.Int("count");
            text = $"{Param("whose")!.Choices.First(c => c.Value == args.Choice("whose")).Label} can only attempt to activate {(n == 1 ? "1 monster effect" : $"up to {n} monster effects")} per turn";
        }
        return text.StartsWith("Each ", StringComparison.Ordinal) ? "each " + text[5..] : text;
    }

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        IEnumerable<string> Player(string v, string code, string self, string opp, string? value = null, string? target = null)
        {
            var lines = new List<string>
            {
                $"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)", $"{v}:SetCode({code})",
                $"{v}:SetRange({range})", $"{v}:SetTargetRange({self},{opp})",
            };
            if (value is not null) lines.Add($"{v}:SetValue({value})");
            if (target is not null) lines.Add($"{v}:SetTarget({target})");
            return lines;
        }
        switch (ctx.Args.Choice("rule"))
        {
            case "no_battle_damage":
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_AVOID_BATTLE_DAMAGE", "1", "0", "1"));
                break;
            case "no_effect_damage":
            {
                var fn = ctx.Script.AddFunction("damval", "e,re,val,r,rp,rc", ["if bit.band(r,REASON_EFFECT)~=0 then return 0 end", "return val"]);
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CHANGE_DAMAGE", "1", "0", fn));
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_NO_EFFECT_DAMAGE", "1", "0"));
                break;
            }
            case "no_damage":
            {
                var fn = ctx.Script.AddFunction("nodamval", "e,re,val,r,rp,rc", ["return 0"]);
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_AVOID_BATTLE_DAMAGE", "1", "0", "1"));
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CHANGE_DAMAGE", "1", "0", fn));
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_NO_EFFECT_DAMAGE", "1", "0"));
                break;
            }
            case "half_damage":
            {
                var fn = ctx.Script.AddFunction("halfdamval", "e,re,val,r,rp,rc", ["return math.floor(val/2)"]);
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CHANGE_DAMAGE", "1", "0", fn));
                break;
            }
            case "reflect_effect":
            {
                var fn = ctx.Script.AddFunction("reflectval", "e,re,val,r,rp,rc", ["return bit.band(r,REASON_EFFECT)~=0"]);
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_REFLECT_DAMAGE", "1", "0", fn));
                break;
            }
            case "opp_double_battle":
            {
                var fn = ctx.Script.AddFunction("doubledamval", "e,re,val,r,rp,rc", ["if bit.band(r,REASON_BATTLE)~=0 then return val*2 end", "return val"]);
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CHANGE_DAMAGE", "0", "1", fn));
                break;
            }
            case "opp_no_effect_damage":
            {
                var fn = ctx.Script.AddFunction("damval", "e,re,val,r,rp,rc", ["if bit.band(r,REASON_EFFECT)~=0 then return 0 end", "return val"]);
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CHANGE_DAMAGE", "0", "1", fn));
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_NO_EFFECT_DAMAGE", "0", "1"));
                break;
            }
            case "opp_no_extra":
            {
                var fn = ctx.Script.AddFunction("sslimit", "e,c", ["return c:IsLocation(LOCATION_EXTRA)"]);
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CANNOT_SPECIAL_SUMMON", "0", "1", null, fn));
                break;
            }
            case "opp_no_special":
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CANNOT_SPECIAL_SUMMON", "0", "1"));
                break;
            case "opp_no_draw_extra":
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CANNOT_TO_HAND", "0", "1", null, ctx.Script.AddFunction("thlimit", "e,c", ["return c:IsLocation(LOCATION_DECK)"])));
                break;
            case "no_own_response":
            {
                // Each activation: only the other player may chain to it (a chain limit set as it is activated).
                var op = ctx.Script.AddFunction("ownchainop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.SetChainLimit(function(ce,cp,ctp) return cp~=rp end)"]);
                ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EVENT_CHAINING)", $"{v}:SetRange({range})", $"{v}:SetOperation({op})"]);
                break;
            }
            case "one_monster_effect":
            {
                // Each player's monster effects this turn leave a flag each on this card; when there are as many as allowed,
                // that player is locked out.
                var flags = new[] { $"id+{8420 + ctx.Script.Slot("onemonster")}", $"id+{8440 + ctx.Script.Slot("onemonster")}" };
                var n = ctx.Args.Int("count");
                var sides = ctx.Args.Choice("whose") switch { "you" => new[] { 0 }, "opponent" => [1], _ => [0, 1] };
                foreach (var side in sides)
                {
                    var flag = flags[side];
                    var mine = side == 0 ? "ep~=tp" : "ep==tp";
                    var count = ctx.Script.AddFunction("onecount", "e,tp,eg,ep,ev,re,r,rp",
                        [$"if {mine} or not re:IsActiveType(TYPE_MONSTER) then return end", $"e:GetHandler():RegisterFlagEffect({flag},RESET_EVENT+0x3ff0000+RESET_PHASE+PHASE_END,0,1)"]);
                    ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EVENT_CHAINING)", $"{v}:SetRange({range})",
                        $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)", $"{v}:SetOperation({count})"]);
                    var con = ctx.Script.AddFunction("onecon", "e", [n == 1 ? $"return e:GetHandler():GetFlagEffect({flag})~=0" : $"return e:GetHandler():GetFlagEffect({flag})>={n}"]);
                    var limit = ctx.Script.AddFunction("onelimit", "e,re,tp", ["return re:IsActiveType(TYPE_MONSTER)"]);
                    ctx.Effect.AddPassive(v => [.. Player(v, "EFFECT_CANNOT_ACTIVATE", side == 0 ? "1" : "0", side == 0 ? "0" : "1", limit), $"{v}:SetCondition({con})"]);
                }
                break;
            }
            case "one_attacker":
            {
                // The first monster to attack in a Battle Phase is remembered; no other monster can attack in it.
                var flag = $"id+{8460 + ctx.Script.Slot("oneattacker")}";
                var con = ctx.Script.AddFunction("oneatkcon", "e", [$"return e:GetHandler():GetFlagEffect({flag})~=0"]);
                var tg = ctx.Script.AddFunction("oneatktg", "e,c", ["return c:GetFieldID()~=e:GetLabel()"]);
                var check = ctx.Script.AddFunction("oneatkop", "e,tp,eg,ep,ev,re,r,rp",
                [
                    $"if e:GetHandler():GetFlagEffect({flag})~=0 then return end",
                    "local fid=eg:GetFirst():GetFieldID()",
                    $"e:GetHandler():RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,0,1)",
                    "e:GetLabelObject():SetLabel(fid)",
                ]);
                // The attack lock and its watcher are one pair: the watcher tells the lock which monster attacked.
                ctx.Effect.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetProperty(EFFECT_FLAG_IGNORE_IMMUNE)", $"{v}:SetCode(EFFECT_CANNOT_ATTACK_ANNOUNCE)",
                    $"{v}:SetRange({range})", $"{v}:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)", $"{v}:SetCondition({con})", $"{v}:SetTarget({tg})",
                    "local oneatk=" + v,
                ]);
                ctx.Effect.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)", $"{v}:SetCode(EVENT_ATTACK_ANNOUNCE)",
                    $"{v}:SetRange({range})", $"{v}:SetOperation({check})", $"{v}:SetLabelObject(oneatk)",
                ]);
                break;
            }
            case "one_per_chain":
            {
                // Each activation leaves a flag on this card for its player until the Chain ends; that player then can't
                // activate another card or effect in that Chain (Angelechy Castellan's script).
                var flag = $"id+{8490 + 2 * ctx.Script.Slot("oneperchain")}";
                var mark = ctx.Script.AddFunction("chainmark", "e,tp,eg,ep,ev,re,r,rp", [$"e:GetHandler():RegisterFlagEffect({flag}+rp,RESET_EVENT+RESETS_STANDARD+RESET_CHAIN,0,1)"]);
                ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)", $"{v}:SetCode(EVENT_CHAINING)",
                    $"{v}:SetRange({range})", $"{v}:SetOperation({mark})"]);
                var limit = ctx.Script.AddFunction("chainlimitval", "e,re,tp", [$"return e:GetHandler():GetFlagEffect({flag}+tp)~=0"]);
                ctx.Effect.AddPassive(v => Player(v, "EFFECT_CANNOT_ACTIVATE", "1", "1", limit));
                break;
            }
            case "opp_no_bp_activate":
            {
                var con = ctx.Script.AddFunction("bpcon", "e", ["local ph=Duel.GetCurrentPhase()", "return ph>=PHASE_BATTLE_START and ph<=PHASE_BATTLE"]);
                ctx.Effect.AddPassive(v => [.. Player(v, "EFFECT_CANNOT_ACTIVATE", "0", "1", "1"), $"{v}:SetCondition({con})"]);
                break;
            }
        }
    }
}

/// <summary>What an Equip Spell gives the monster it is equipped to.</summary>
public sealed class EquippedPassive : PassiveBlock
{
    public const string BlockId = "passive.equipped";
    public override string Id => BlockId;
    public override string Group => "Equip";
    public override string Name => "The equipped monster gains ATK / is protected...";
    public override string Sentence => "The equipped monster {only} {equipped} {rule} {amount}";
    public override string Help => "For Equip Spells (Card tab: Spell kind \"Equip\"). Card Crafter writes the activation that equips the card. " +
                                   "\"Only one like this\" applies it only while the equipped monster is such a monster: \"A \"Therion\" monster equipped with this card gains 700 ATK\".";

    /// <summary>"A "Therion" monster equipped with this card": only while the equipped monster is one. Saved before this was offered: any monster.</summary>
    public static ParamSpec OnlyParam => ParamSpec.Choice("only", "Which equipped monster", new("any", "", "(any monster)"), new("cards", "", "only one like this:"));

    public static ParamSpec EquippedParam => ParamSpec.Cards("equipped", "The equipped monster",
            () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster })
        .Unprinted().ShowIf(b => b.Values.GetValueOrDefault("only") == "cards");

    /// <summary>"the equipped monster", or "a "Therion" monster equipped with this card".</summary>
    public static string Subject(BlockArgs args)
    {
        if (args.Choice("only") != "cards") return "the equipped monster";
        var f = args.Cards("equipped").Clone();
        f.Zones = Zone.None;
        f.CardType = FilterCardType.Monster;
        var noun = FilterText.Describe(f, args.Pack, withLocation: false, ownName: args.CardName);
        var article = noun.Length > 0 && "AEIOUaeiou".Contains(noun.TrimStart('"')[0]) && !noun.StartsWith("Us", StringComparison.Ordinal) ? "an" : "a";
        return $"{article} {noun} equipped with this card";
    }

    /// <summary>The Lua test that <paramref name="card"/> is the monster the picker allows, or null for any monster.</summary>
    public static string? EquippedTest(BlockContext ctx, string card)
    {
        if (ctx.Args.Choice("only") != "cards") return null;
        var f = ctx.Args.Cards("equipped").Clone();
        f.Zones = Zone.None;
        f.Owner = CardOwner.Either;
        f.CardType = FilterCardType.Monster;
        return $"{ctx.Script.Filter(f, null, "eqmfilter").Function}({card},e,e:GetHandlerPlayer())";
    }

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        OnlyParam,
        EquippedParam,
        ParamSpec.Choice("rule", "Rule",
            new("gain_atk", "gains", "gains ATK"),
            new("lose_atk", "loses", "loses ATK"),
            new("gain_both", "gains", "gains ATK/DEF"),
            new("indestructible_battle", "cannot be destroyed by battle"),
            new("indestructible_effect", "cannot be destroyed by card effects"),
            new("cannot_attack", "cannot attack"),
            new("negated", "has its effects negated"),
            new("pierce", "inflicts piercing battle damage"),
            new("untargetable", "cannot be targeted by your opponent's card effects"),
            new("gain_def", "gains", "gains DEF"),
            new("lose_def", "loses", "loses DEF"),
            new("direct", "can attack directly"),
            new("cannot_direct", "cannot attack directly"),
            new("substitute", "would be destroyed, destroy this card instead", "is destroyed: destroy this card instead"),
            new("substitute_battle", "would be destroyed by battle, destroy this card instead", "is destroyed by battle: destroy this card instead")),
        ParamSpec.Int("amount", "ATK/DEF amount", 500, 0, 99999),
    ];

    public override string Describe(BlockArgs args) => args.Choice("rule") switch
    {
        "gain_atk" => $"{Subject(args)} gains {args.Int("amount")} ATK",
        "lose_atk" => $"{Subject(args)} loses {args.Int("amount")} ATK",
        "gain_both" => $"{Subject(args)} gains {args.Int("amount")} ATK/DEF",
        "gain_def" => $"{Subject(args)} gains {args.Int("amount")} DEF",
        "lose_def" => $"{Subject(args)} loses {args.Int("amount")} DEF",
        "substitute" or "substitute_battle" => $"if {Subject(args)} {args.Describe("rule")}",
        _ => $"{Subject(args)} {args.Describe("rule")}",
    };

    public override void Compile(BlockContext ctx)
    {
        // Equip Spells, and Union monsters or Traps that equip themselves by an effect.
        if (ctx.Card.Kind == CardKind.Spell && ctx.Card.SpellKind != SpellKind.Equip)
            ctx.Effect.Error("\"The equipped monster ...\" needs an Equip Spell: set the Spell kind to Equip on the Card tab.");
        var n = ctx.Args.Int("amount");
        // "A "Therion" monster equipped with this card gains 700 ATK": only while the equipped monster is one.
        var only = EquippedTest(ctx, "ec") is { } test ? $"(function(ec) return ec~=nil and {test} end)(e:GetHandler():GetEquipTarget())" : null;
        // "If the equipped monster would be destroyed, destroy this card instead": it works even against effects the monster is immune to.
        if (ctx.Args.Choice("rule") is "substitute" or "substitute_battle")
        {
            var value = ctx.Args.Choice("rule") == "substitute" ? "1" : ctx.Script.AddFunction("substval", "e,re,r,rp", ["return bit.band(r,REASON_BATTLE)~=0"]);
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_EQUIP)", $"{v}:SetProperty(EFFECT_FLAG_IGNORE_IMMUNE)", $"{v}:SetCode(EFFECT_DESTROY_SUBSTITUTE)", $"{v}:SetValue({value})"], condition: only);
            return;
        }
        var codes = ctx.Args.Choice("rule") switch
        {
            "gain_atk" => new[] { ("EFFECT_UPDATE_ATTACK", n.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            "lose_atk" => [("EFFECT_UPDATE_ATTACK", (-n).ToString(System.Globalization.CultureInfo.InvariantCulture))],
            "gain_both" => [("EFFECT_UPDATE_ATTACK", n.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("EFFECT_UPDATE_DEFENSE", n.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            "indestructible_battle" => [("EFFECT_INDESTRUCTABLE_BATTLE", "1")],
            "indestructible_effect" => [("EFFECT_INDESTRUCTABLE_EFFECT", "1")],
            "cannot_attack" => [("EFFECT_CANNOT_ATTACK", "1")],
            "negated" => [("EFFECT_DISABLE", "1"), ("EFFECT_DISABLE_EFFECT", "1")],
            "pierce" => [("EFFECT_PIERCE", "1")],
            "gain_def" => [("EFFECT_UPDATE_DEFENSE", n.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            "lose_def" => [("EFFECT_UPDATE_DEFENSE", (-n).ToString(System.Globalization.CultureInfo.InvariantCulture))],
            "direct" => [("EFFECT_DIRECT_ATTACK", "1")],
            "cannot_direct" => [("EFFECT_CANNOT_DIRECT_ATTACK", "1")],
            _ => [("EFFECT_CANNOT_BE_EFFECT_TARGET", "aux.tgoval")],
        };
        foreach (var (code, value) in codes)
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_EQUIP)", $"{v}:SetCode({code})", $"{v}:SetValue({value})"], condition: only);
    }
}

// ---------------------------------------------------------------- conditions

public sealed class PositionCondition : ConditionBlock
{
    public override string Id => "if.position";
    public override string Group => "This card";
    public override string Name => "This card is in a position";
    public override string Sentence => "This card is in {pos}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("pos", "Position",
            new("attack", "Attack Position"), new("defense", "Defense Position"), new("faceup_defense", "face-up Defense Position")),
    ];

    public override string Describe(BlockArgs args) => $"this card is in {args.Describe("pos")}";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        ctx.Effect.Conditions.Add(ctx.Args.Choice("pos") switch
        {
            "defense" => "e:GetHandler():IsDefensePos()",
            "faceup_defense" => "e:GetHandler():IsPosition(POS_FACEUP_DEFENSE)",
            _ => "e:GetHandler():IsAttackPos()",
        });
    }
}

public sealed class HandCountCondition : ConditionBlock
{
    public override string Id => "if.hand_count";
    public override string Group => "Cards";
    public override string Name => "Number of cards in a hand";
    public override string Sentence => "{who} {cmp} {count} cards in hand";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "you have"), new("opponent", "your opponent has")),
        ParamSpec.Choice("cmp", "Compare", new("le", "or fewer"), new("ge", "or more"), new("eq", "exactly")),
        ParamSpec.Int("count", "How many", 0, 0, 20),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var who = args.Describe("who");
        var cards = n == 1 ? "card" : "cards";
        if (n == 0 && args.Choice("cmp") != "ge") return args.Choice("who") == "you" ? "you have no cards in your hand" : "your opponent has no cards in their hand";
        return args.Choice("cmp") switch
        {
            "eq" => $"{who} exactly {n} {cards} in {(args.Choice("who") == "you" ? "your" : "their")} hand",
            "ge" => $"{who} {n} or more {cards} in {(args.Choice("who") == "you" ? "your" : "their")} hand",
            _ => $"{who} {n} or fewer {cards} in {(args.Choice("who") == "you" ? "your" : "their")} hand",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var p = ctx.Args.Choice("who") == "you" ? "tp" : "1-tp";
        var op = ctx.Args.Choice("cmp") switch { "ge" => ">=", "eq" => "==", _ => "<=" };
        ctx.Effect.Conditions.Add($"Duel.GetFieldGroupCount({p},LOCATION_HAND,0){op}{ctx.Args.Int("count")}");
    }
}

public sealed class SummonedByCondition : ConditionBlock
{
    public override string Id => "if.summoned_by";
    public override string Group => "This card";
    public override string Name => "How this card was Summoned";
    public override string Sentence => "This card was {how}";
    public override string Help => "For example, an effect that only works if this card was Link Summoned, or Special Summoned from the hand.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("link", "Link Summoned"), new("synchro", "Synchro Summoned"), new("xyz", "Xyz Summoned"),
            new("fusion", "Fusion Summoned"), new("ritual", "Ritual Summoned"), new("pendulum", "Pendulum Summoned"),
            new("normal", "Normal Summoned"), new("tribute", "Tribute Summoned"), new("from_hand", "Special Summoned from the hand"),
            new("from_gy", "Special Summoned from the GY"), new("from_extra", "Special Summoned from the Extra Deck"),
            new("self", "Special Summoned by its own effect", "Special Summoned by its own effect or procedure (\"this way\")"),
            new("by_synchro_effect", "Special Summoned by the effect of a Synchro Monster")),
    ];

    public override string Describe(BlockArgs args) => $"this card was {args.Describe("how")}";

    public static string Check(string how) => how switch
    {
        "synchro" => "e:GetHandler():IsSummonType(SUMMON_TYPE_SYNCHRO)",
        "xyz" => "e:GetHandler():IsSummonType(SUMMON_TYPE_XYZ)",
        "fusion" => "e:GetHandler():IsSummonType(SUMMON_TYPE_FUSION)",
        "ritual" => "e:GetHandler():IsSummonType(SUMMON_TYPE_RITUAL)",
        "pendulum" => "e:GetHandler():IsSummonType(SUMMON_TYPE_PENDULUM)",
        "normal" => "e:GetHandler():IsSummonType(SUMMON_TYPE_NORMAL)",
        "tribute" => "e:GetHandler():IsSummonType(SUMMON_TYPE_ADVANCE)",
        "from_hand" => "e:GetHandler():IsSummonType(SUMMON_TYPE_SPECIAL) and e:GetHandler():IsSummonLocation(LOCATION_HAND)",
        "from_gy" => "e:GetHandler():IsSummonType(SUMMON_TYPE_SPECIAL) and e:GetHandler():IsSummonLocation(LOCATION_GRAVE)",
        "from_extra" => "e:GetHandler():IsSummonType(SUMMON_TYPE_SPECIAL) and e:GetHandler():IsSummonLocation(LOCATION_EXTRA)",
        "self" => "e:GetHandler():GetSummonType()==SUMMON_TYPE_SPECIAL+SUMMON_VALUE_SELF",
        "by_synchro_effect" => "s.cc_bysynchro(e:GetHandler())",
        _ => "e:GetHandler():IsSummonType(SUMMON_TYPE_LINK)",
    };

    /// <summary>The flag a watcher on this card sets when a Synchro Monster's effect Special Summons it (the Yummy monsters).</summary>
    public const string SynchroFlag = "id+8200";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        if (ctx.Args.Choice("how") == "by_synchro_effect") WatchSynchroSummon(ctx.Script);
        ctx.Effect.Conditions.Add(Check(ctx.Args.Choice("how")));
    }

    /// <summary>
    /// As the Yummy monsters do: when this card is Special Summoned by a monster effect of a Synchro Monster, it keeps a
    /// flag while it stays on the field.
    /// </summary>
    private static void WatchSynchroSummon(ScriptBuilder script)
    {
        var reg = script.AddFunction("synreg", "e,tp,eg,ep,ev,re,r,rp",
        [
            "if not re then return end",
            "if re:IsActiveType(TYPE_MONSTER) and re:GetHandler():IsType(TYPE_SYNCHRO) then",
            $"\te:GetHandler():RegisterFlagEffect({SynchroFlag},RESET_EVENT+RESETS_STANDARD-RESET_TOFIELD-RESET_TEMP_REMOVE,0,1)",
            "end",
        ]);
        // A trigger on the Summon itself is checked before the watcher has run: the effect that Summoned the card says it then.
        script.AddFunction("cc_bysynchro", "c",
        [
            $"if c:GetFlagEffect({SynchroFlag})>0 then return true end",
            "local se=c:GetReasonEffect()",
            "return c:IsSummonType(SUMMON_TYPE_SPECIAL) and se~=nil and se:IsActiveType(TYPE_MONSTER) and se:GetHandler():IsType(TYPE_SYNCHRO)",
        ]);
        script.AddInitialLine(string.Join("\n\t",
        [
            "local sy=Effect.CreateEffect(c)",
            "sy:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS)",
            "sy:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            "sy:SetCode(EVENT_SPSUMMON_SUCCESS)",
            $"sy:SetOperation({reg})",
            "c:RegisterEffect(sy)",
        ]));
    }
}

/// <summary>
/// The game's check that the turn player can still enter the Battle Phase, for effects like "this card can attack twice
/// this turn". Card text never says it, so it adds no words.
/// </summary>
public sealed class CanBattleCondition : ConditionBlock
{
    public override string Id => "if.can_battle";
    public override string Group => "Turn";
    public override string Name => "The Battle Phase is still ahead";
    public override string Sentence => "You can still enter the Battle Phase this turn";
    public override string Help => "Only activates before the Battle Phase, when there will be one. Card text doesn't say this, so it adds no words.";
    public override string Describe(BlockArgs args) => "";
    public override void Compile(BlockContext ctx) => ctx.Effect.Conditions.Add("Duel.IsAbleToEnterBP()");
}

/// <summary>"Except the turn this card was sent to the GY", for effects used from the GY.</summary>
public sealed class NotSentThisTurnCondition : ConditionBlock
{
    public override string Id => "if.not_sent_this_turn";
    public override string Group => "This card";
    public override string Name => "This card wasn't sent to the GY this turn";
    public override string Sentence => "This card was not sent to the GY this turn";
    public override string Help => "For GY effects that can't be used the turn the card got there.";

    public override string Describe(BlockArgs args) => "this card was not sent to the GY this turn";

    public override void Compile(BlockContext ctx) => ctx.Effect.Conditions.Add("aux.exccon(e,tp,eg,ep,ev,re,r,rp)");
}

/// <summary>"This card gains 300 ATK for each Dragon monster in your GY."</summary>
public sealed class SelfStatsPerCardPassive : PassiveBlock
{
    public override string Id => "passive.self_stats_per";
    public override string Group => "This card";
    public override string Name => "This card gains ATK/DEF for each card";
    public override string Sentence => "This card {dir} {amount} {stat} for each {per} {counter} {counter_id} {cards}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("dir", "Gains/loses", new("gain", "gains"), new("lose", "loses")),
        ParamSpec.Int("amount", "Amount", 300, 1, 99999),
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF")),
        ParamSpec.Choice("per", "For each",
            new("cards", "", "card picked below"),
            new("materials", "material attached to it", "material attached to it (Xyz)"),
            new("level", "Level it has", "of its Levels (Level x amount)"),
            new("linked", "monster it points to", "monster it points to (Link)"),
            new("counters", "counter on it", "counter on it (pick which below)")),
        // Saved before counters had a kind, "for each counter on it" is every counter: "any" is the default.
        .. Counters.Params(withAny: true, b => b.Values.GetValueOrDefault("per") == "counters"),
        ParamSpec.Cards("cards", "For each card (when \"card picked below\")", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }),
    ];

    public override string Describe(BlockArgs args)
    {
        var head = $"this card {args.Describe("dir")} {args.Int("amount")} {args.Describe("stat")}";
        return args.Choice("per") switch
        {
            "materials" => $"{head} for each material attached to it",
            "level" => $"{head} for each of its Levels",
            "linked" => $"{head} for each monster it points to",
            "counters" => $"{head} for each {Counters.Name(args)} on it",
            _ => $"{head} for each {Text.FilterText.Describe(args.Cards("cards"), args.Pack, preposition: "in", ownName: args.CardName)}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var amount = (ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "lose" ? -1 : 1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string count;
        switch (ctx.Args.Choice("per"))
        {
            case "materials": count = "c:GetOverlayCount()"; break;
            case "level": count = "c:GetLevel()"; break;
            case "linked": count = "c:GetLinkedGroupCount()"; break;
            // GetCounter(0) is how many kinds of counter there are, not how many counters.
            case "counters":
                count = Counters.On(ctx.Script, "c", Counters.Id(ctx.Args));
                // A card that gains ATK for its own counters can hold them.
                if (Counters.Id(ctx.Args) is { } id) Counters.Permit(ctx, id);
                break;
            default:
                var f = ctx.Script.Filter(ctx.Args.Cards("cards"));
                count = $"Duel.GetMatchingGroupCount({f.Function},e:GetHandlerPlayer(),{f.Self},{f.Opp},{(f.Exclude == "nil" ? "nil" : "e:GetHandler()")},e,e:GetHandlerPlayer())";
                break;
        }
        var value = ctx.Script.AddFunction("perval", "e,c", [$"return {count}*{amount}"]);
        var codes = ctx.Args.Choice("stat") switch
        {
            "def" => new[] { "EFFECT_UPDATE_DEFENSE" },
            "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
            _ => ["EFFECT_UPDATE_ATTACK"],
        };
        var range = RangeOf(ctx);
        foreach (var code in codes) ctx.Effect.AddPassive(v => SelfEffect(v, range, code, value));
    }
}

/// <summary>
/// "You cannot Special Summon monsters the turn you activate this effect, except ...": put on the turn when the effect
/// is activated, so it applies even if the effect is negated.
/// </summary>
public sealed class SummonRestrictionCost : CostBlock
{
    public override string Id => "cost.summon_restriction";
    public override string Group => "Turn";
    public override string Name => "Lock: you can't Special Summon / activate / attack this turn, except...";
    public override string Sentence => "You cannot {what} {when}, except {cards}";
    public override string Help => "Ryzeal-style locks: \"You cannot Special Summon from the Extra Deck the turn you activate this effect, except Xyz Monsters\". " +
                                   "\"The turn you activate this\" also stops the effect if you already did it this turn; \"after activating\" only locks what comes after. " +
                                   "On a Special Summon procedure it reads \"If Summoned this way...\". The card text puts it after the effect.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "Cannot", Locks.TurnWhat),
        ParamSpec.Choice("when", "When", new("rest", "for the rest of the turn after activating"), new("whole_turn", "the turn you activate this")),
        ParamSpec.Cards("cards", "Except (plain monsters for none)", () => new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark }, Zone.MonsterZone),
    ];

    public override string Describe(BlockArgs args) => "";

    public override string Afterword(BlockArgs args)
    {
        var what = Locks.WhatOf(args);
        var (head, except) = Locks.Split(what, args.Cards("cards"), args);
        var procedure = args.Effect?.When?.BlockId == SummonProcedureTrigger.BlockId;
        var thing = DetailedTriggers.ActivatesThisCard(args.Effect) ? "card" : "effect";
        if (args.Choice("when") == "whole_turn")
            return procedure
                ? $"{Text.SentenceCase.Upper(head)} the turn you Special Summon this card this way{except}."
                : $"{Text.SentenceCase.Upper(head)} the turn you activate this {thing}{except}.";
        return procedure
            ? $"If Summoned this way, {head} for the rest of this turn{except}."
            : $"For the rest of this turn after this {thing} is activated, {head}{except}.";
    }

    public override void Compile(BlockContext ctx)
    {
        var what = Locks.WhatOf(ctx.Args);
        var except = ctx.Args.Cards("cards");
        if (ctx.Args.Choice("when") == "whole_turn" && Locks.TurnCheck(ctx, what, except) is { } check)
            ctx.Effect.CostChecks.Add(check);
        // "Normal or Special Summon" is one engine effect for each kind of Summon.
        for (var part = 0; part < Locks.Parts(what); part++)
        {
            var v = part == 0 ? "sl" : $"sl{part + 1}";
            ctx.Effect.CostLines.Add($"local {v}=Effect.CreateEffect(e:GetHandler())");
            ctx.Effect.CostLines.AddRange(Locks.Lines(ctx, v, "you", what, except, null, "EFFECT_FLAG_OATH", part));
            ctx.Effect.CostLines.Add($"{v}:SetReset(RESET_PHASE+PHASE_END)");
            ctx.Effect.CostLines.Add($"Duel.RegisterEffect({v},tp)");
        }
    }
}
