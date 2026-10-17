using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "For the rest of this turn, monsters you control gain 500 ATK" and the like: an effect on the field, not on
/// chosen monsters, so it also applies to monsters that arrive later in the turn.
/// </summary>
public sealed class TurnFieldAction : ActionBlock
{
    public const string BlockId = "do.turn_field";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "For the rest of this turn, monsters on the field gain ATK / can't be destroyed...";
    public override string Sentence => "For the rest of this turn, {cards} {rule} {amount}";
    public override string Help => "Applies to every monster the picker allows, including ones Summoned later this turn.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Choice("rule", "Rule",
            new("gain_atk", "gain ATK:"),
            new("gain_def", "gain DEF:"),
            new("gain_both", "gain ATK/DEF:"),
            new("lose_atk", "lose ATK:"),
            new("lose_both", "lose ATK/DEF:"),
            new("indestructible_battle", "cannot be destroyed by battle"),
            new("indestructible_effect", "cannot be destroyed by card effects"),
            new("indestructible_both", "cannot be destroyed by battle or card effects"),
            new("untargetable", "cannot be targeted by your opponent's card effects"),
            new("unaffected", "are unaffected by your opponent's card effects"),
            new("cannot_attack", "cannot attack"),
            new("pierce", "inflict piercing battle damage"),
            new("direct", "can attack directly"),
            new("negated", "have their effects negated"),
            new("cannot_direct", "cannot attack directly")),
        ParamSpec.Int("amount", "Amount", 500, 1, 99999).ShowIf(b => b.Values.GetValueOrDefault("rule", "gain_atk") is "gain_atk" or "gain_def" or "gain_both" or "lose_atk" or "lose_both"),
    ];

    private static bool Stat(string rule) => rule is "gain_atk" or "gain_def" or "gain_both" or "lose_atk" or "lose_both";

    public override string Describe(BlockArgs args)
    {
        var rule = args.Choice("rule");
        var who = FilterText.Describe(args.Cards("cards"), args.Pack, plural: true, preposition: "");
        var what = rule switch
        {
            "gain_atk" => $"gain {args.Int("amount")} ATK",
            "gain_def" => $"gain {args.Int("amount")} DEF",
            "gain_both" => $"gain {args.Int("amount")} ATK/DEF",
            "lose_atk" => $"lose {args.Int("amount")} ATK",
            "lose_both" => $"lose {args.Int("amount")} ATK/DEF",
            _ => args.Describe("rule"),
        };
        return $"for the rest of this turn, {who} {what}";
    }

    public override void Compile(BlockContext ctx)
    {
        var rule = ctx.Args.Choice("rule");
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("turntg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetLabelObject()" : "")]);
        var n = ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var (codes, value) = rule switch
        {
            "gain_atk" => (new[] { "EFFECT_UPDATE_ATTACK" }, n),
            "gain_def" => (["EFFECT_UPDATE_DEFENSE"], n),
            "gain_both" => (["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"], n),
            "lose_atk" => (["EFFECT_UPDATE_ATTACK"], "-" + n),
            "lose_both" => (["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"], "-" + n),
            "indestructible_battle" => (["EFFECT_INDESTRUCTABLE_BATTLE"], "1"),
            "indestructible_effect" => (["EFFECT_INDESTRUCTABLE_EFFECT"], "1"),
            "indestructible_both" => (["EFFECT_INDESTRUCTABLE_BATTLE", "EFFECT_INDESTRUCTABLE_EFFECT"], "1"),
            "untargetable" => (["EFFECT_CANNOT_BE_EFFECT_TARGET"], "aux.tgoval"),
            "unaffected" => (["EFFECT_IMMUNE_EFFECT"], ctx.Script.AddFunction("turnimmune", "e,te", ["return te:GetOwnerPlayer()~=e:GetHandlerPlayer()"])),
            "pierce" => (["EFFECT_PIERCE"], "1"),
            "direct" => (["EFFECT_DIRECT_ATTACK"], "1"),
            "negated" => (["EFFECT_DISABLE", "EFFECT_DISABLE_EFFECT"], "1"),
            "cannot_direct" => (["EFFECT_CANNOT_DIRECT_ATTACK"], "1"),
            _ => (["EFFECT_CANNOT_ATTACK"], "1"),
        };
        if (Stat(rule)) ctx.Effect.AddCategory(rule.Contains("def") && !rule.Contains("both") ? "CATEGORY_DEFCHANGE" : "CATEGORY_ATKCHANGE");
        var lines = new List<string>();
        for (var i = 0; i < codes.Length; i++)
        {
            var v = $"tf{i + 1}";
            lines.Add($"local {v}=Effect.CreateEffect(e:GetHandler())");
            lines.Add($"{v}:SetType(EFFECT_TYPE_FIELD)");
            lines.Add($"{v}:SetCode({codes[i]})");
            lines.Add($"{v}:SetTargetRange({f.Self},{f.Opp})");
            lines.Add($"{v}:SetTarget({target})");
            if (value != "1" || codes[i] is not ("EFFECT_DISABLE" or "EFFECT_DISABLE_EFFECT" or "EFFECT_CANNOT_ATTACK" or "EFFECT_CANNOT_DIRECT_ATTACK")) lines.Add($"{v}:SetValue({value})");
            if (filter.ExcludeThisCard) lines.Add($"{v}:SetLabelObject(e:GetHandler())");
            lines.Add($"{v}:SetReset(RESET_PHASE+PHASE_END)");
            lines.Add($"Duel.RegisterEffect({v},tp)");
        }
        ctx.Effect.AddStep(lines, null, ctx.Link);
    }
}

/// <summary>"For the rest of this turn, you take no battle damage", "... any damage you take is halved".</summary>
public sealed class TurnPlayerRuleAction : ActionBlock
{
    public const string BlockId = "do.turn_player";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "For the rest of this turn, you take no damage / half damage...";
    public override string Sentence => "For the rest of this turn, {rule}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("rule", "Rule",
            new("no_battle", "you take no battle damage"),
            new("no_effect", "you take no effect damage"),
            new("no_damage", "you take no damage"),
            new("half", "any damage you take is halved"),
            new("opp_double_battle", "battle damage your opponent takes is doubled"),
            new("opp_half_battle", "battle damage your opponent takes is halved"),
            new("reflect", "effect damage you would take is inflicted to your opponent instead"),
            new("opp_no_battle", "your opponent takes no battle damage"),
            new("reveal_hand", "you must keep your hand revealed"),
            // The Secret Crane Princess Who Returns A Favor: "you must keep your hand revealed during this Main Phase".
            new("reveal_hand_main", "you must keep your hand revealed during this Main Phase", "(this Main Phase only) you must keep your hand revealed")),
    ];

    public override string Describe(BlockArgs args) =>
        args.Choice("rule") == "reveal_hand_main" ? "you must keep your hand revealed during this Main Phase" : $"for the rest of this turn, {args.Describe("rule")}";

    public override void Compile(BlockContext ctx)
    {
        var lines = new List<string>();
        var i = 0;
        void Player(string code, string self, string opp, string? value)
        {
            var v = $"tp{++i}";
            lines.Add($"local {v}=Effect.CreateEffect(e:GetHandler())");
            lines.Add($"{v}:SetType(EFFECT_TYPE_FIELD)");
            lines.Add($"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)");
            lines.Add($"{v}:SetCode({code})");
            lines.Add($"{v}:SetTargetRange({self},{opp})");
            if (value is not null) lines.Add($"{v}:SetValue({value})");
            lines.Add($"{v}:SetReset(RESET_PHASE+PHASE_END)");
            lines.Add($"Duel.RegisterEffect({v},tp)");
        }
        string Fn(string name, string body) => ctx.Script.AddFunction(name, "e,re,val,r,rp,rc", [body]);
        switch (ctx.Args.Choice("rule"))
        {
            case "no_battle": Player("EFFECT_AVOID_BATTLE_DAMAGE", "1", "0", "1"); break;
            case "opp_no_battle": Player("EFFECT_AVOID_BATTLE_DAMAGE", "0", "1", "1"); break;
            case "no_effect":
                Player("EFFECT_CHANGE_DAMAGE", "1", "0", Fn("damval", "if bit.band(r,REASON_EFFECT)~=0 then return 0 end return val"));
                Player("EFFECT_NO_EFFECT_DAMAGE", "1", "0", null);
                break;
            case "no_damage":
                Player("EFFECT_AVOID_BATTLE_DAMAGE", "1", "0", "1");
                Player("EFFECT_CHANGE_DAMAGE", "1", "0", Fn("nodamval", "return 0"));
                Player("EFFECT_NO_EFFECT_DAMAGE", "1", "0", null);
                break;
            case "half": Player("EFFECT_CHANGE_DAMAGE", "1", "0", Fn("halfdamval", "return math.floor(val/2)")); break;
            case "reveal_hand" or "reveal_hand_main":
            {
                // Your hand is public (EFFECT_PUBLIC) until the end of the turn, or of the Main Phase it is used in.
                var reset = ctx.Args.Choice("rule") == "reveal_hand" ? "RESET_PHASE+PHASE_END"
                    : "RESET_PHASE+((Duel.GetCurrentPhase()==PHASE_MAIN1 or Duel.GetCurrentPhase()==PHASE_MAIN2) and Duel.GetCurrentPhase() or PHASE_END)";
                lines.AddRange(
                [
                    "local rh=Effect.CreateEffect(e:GetHandler())",
                    "rh:SetType(EFFECT_TYPE_FIELD)",
                    "rh:SetCode(EFFECT_PUBLIC)",
                    "rh:SetTargetRange(LOCATION_HAND,0)",
                    $"rh:SetReset({reset})",
                    "Duel.RegisterEffect(rh,tp)",
                ]);
                break;
            }
            case "opp_double_battle": Player("EFFECT_CHANGE_DAMAGE", "0", "1", Fn("doubledamval", "if bit.band(r,REASON_BATTLE)~=0 then return val*2 end return val")); break;
            case "opp_half_battle": Player("EFFECT_CHANGE_DAMAGE", "0", "1", Fn("halfbattleval", "if bit.band(r,REASON_BATTLE)~=0 then return math.floor(val/2) end return val")); break;
            default: Player("EFFECT_REFLECT_DAMAGE", "1", "0", Fn("reflectval", "return bit.band(r,REASON_EFFECT)~=0")); break;
        }
        ctx.Effect.AddStep(lines, null, ctx.Link);
    }
}

/// <summary>
/// "For the rest of this turn, each time your opponent Special Summons a monster(s), inflict 500 damage to them": a
/// watcher on the player that answers each time the event happens, without starting a chain.
/// </summary>
public sealed class TurnWatchAction : ActionBlock
{
    public const string BlockId = "do.turn_watch";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "For the rest of this turn, each time something happens, do something";
    public override string Sentence => "For the rest of this turn, each time {event}, {then} {amount}";
    public override string Help => "It answers at once each time, without starting a chain, until the End Phase.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "Each time", EventTrigger.Events.Select(e => new ChoiceOption(e.Key, e.Text)).ToArray()),
        ParamSpec.Choice("then", "Then",
            new("damage", "inflict damage to your opponent:"),
            new("damage_you", "you take damage:"),
            new("gain", "gain LP:"),
            new("draw", "draw 1 card"),
            new("opp_draw", "your opponent draws 1 card")),
        ParamSpec.Int("amount", "Amount", 500, 1, 99999).ShowIf(b => b.Values.GetValueOrDefault("then", "damage") is "damage" or "damage_you" or "gain"),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var then = args.Choice("then") switch
        {
            "damage_you" => $"you take {n} damage",
            "gain" => $"you gain {n} LP",
            "draw" => "draw 1 card",
            "opp_draw" => "your opponent draws 1 card",
            _ => $"inflict {n} damage to your opponent",
        };
        return $"for the rest of this turn, each time {EventTrigger.Find(args.Choice("event")).Text}, {then}";
    }

    public override void Compile(BlockContext ctx)
    {
        var ev = EventTrigger.Find(ctx.Args.Choice("event"));
        var check = ev.Check;
        if (check is not null && check.IndexOf('@') is var at and >= 0)
        {
            var colon = check.IndexOf(':', at);
            var fn = ctx.Script.AddFunction(check[(at + 1)..colon], "c,tp", [$"return {check[(colon + 1)..]}"]);
            check = check[..at] + $"eg:IsExists({fn},1,nil,tp)";
        }
        var n = ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var act = ctx.Args.Choice("then") switch
        {
            "damage_you" => $"Duel.Damage(tp,{n},REASON_EFFECT)",
            "gain" => $"Duel.Recover(tp,{n},REASON_EFFECT)",
            "draw" => "Duel.Draw(tp,1,REASON_EFFECT)",
            "opp_draw" => "Duel.Draw(1-tp,1,REASON_EFFECT)",
            _ => $"Duel.Damage(1-tp,{n},REASON_EFFECT)",
        };
        var con = check is null ? null : ctx.Script.AddFunction("watchcon", "e,tp,eg,ep,ev,re,r,rp", [$"return {check}"]);
        var op = ctx.Script.AddFunction("watchop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.Hint(HINT_CARD,0,id)", act]);
        var lines = new List<string>
        {
            "local tw=Effect.CreateEffect(e:GetHandler())",
            "tw:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            $"tw:SetCode({ev.Code})",
        };
        if (con is not null) lines.Add($"tw:SetCondition({con})");
        lines.AddRange([$"tw:SetOperation({op})", "tw:SetReset(RESET_PHASE+PHASE_END)", "Duel.RegisterEffect(tw,tp)"]);
        ctx.Effect.AddStep(lines, null, ctx.Link);
    }
}

/// <summary>
/// "Each time your opponent Special Summons a monster(s), inflict 200 damage to them" while this card is face-up: it
/// answers at once, without starting a chain (a Continuous effect), like "each time" on cards such as Lava Golem's foes.
/// </summary>
public sealed class EachTimePassive : PassiveBlock
{
    public const string BlockId = "passive.each_time";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "Each time something happens, do something (no chain)";
    public override string Sentence => "Each time {event}, {then} {amount}";
    public override string Help => "A Continuous effect: it applies at once and can't be chained to. The effect's IF blocks and once per turn apply to it.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "Each time", EventTrigger.Events.Select(e => new ChoiceOption(e.Key, e.Text)).ToArray()),
        ParamSpec.Choice("then", "Then",
            new("damage", "inflict damage to your opponent:"),
            new("damage_you", "you take damage:"),
            new("gain", "gain LP:"),
            new("opp_gain", "your opponent gains LP:")),
        ParamSpec.Int("amount", "Amount", 200, 1, 99999),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var then = args.Choice("then") switch
        {
            "damage_you" => $"you take {n} damage",
            "gain" => $"you gain {n} LP",
            "opp_gain" => $"your opponent gains {n} LP",
            _ => $"inflict {n} damage to your opponent",
        };
        return $"each time {EventTrigger.Find(args.Choice("event")).Text}, {then}";
    }

    public override void Compile(BlockContext ctx)
    {
        var ev = EventTrigger.Find(ctx.Args.Choice("event"));
        var check = ev.Check;
        if (check is not null && check.IndexOf('@') is var at and >= 0)
        {
            var colon = check.IndexOf(':', at);
            var fn = ctx.Script.AddFunction(check[(at + 1)..colon], "c,tp", [$"return {check[(colon + 1)..]}"]);
            check = check[..at] + $"eg:IsExists({fn},1,nil,tp)";
        }
        var n = ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var act = ctx.Args.Choice("then") switch
        {
            "damage_you" => $"Duel.Damage(tp,{n},REASON_EFFECT)",
            "gain" => $"Duel.Recover(tp,{n},REASON_EFFECT)",
            "opp_gain" => $"Duel.Recover(1-tp,{n},REASON_EFFECT)",
            _ => $"Duel.Damage(1-tp,{n},REASON_EFFECT)",
        };
        // The event's own check goes in the operation: the effect's condition is its IF blocks.
        var body = new List<string>();
        if (check is not null) body.Add($"if not ({check}) then return end");
        body.AddRange(["Duel.Hint(HINT_CARD,0,id)", act]);
        var op = ctx.Script.AddFunction("eachop", "e,tp,eg,ep,ev,re,r,rp", body);
        var range = RangeOf(ctx);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            $"{v}:SetCode({ev.Code})",
            $"{v}:SetRange({range})",
            $"{v}:SetOperation({op})",
        ], limited: true);
    }
}
