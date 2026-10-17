using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Cyber Emergency's "If the activation of this card in its owner's possession was negated by your opponent's card
/// effect and sent to your GY" (the activation's watcher cost and the GY trigger with its clone), "banish it, then return
/// it to the field" (Interdimensional Matter Forwarder), and the lock on monster effects in the hand or GY.
/// </summary>
public static partial class ScriptTranslator
{
    // ---------------------------------------------------------------- the activation was negated

    /// <summary>A script's top-level functions by name (without the prefix), for parts that look beyond one piece.</summary>
    private static Dictionary<string, string> ScriptFunctions(string lua) =>
        Regex.Matches(lua.Replace("\r\n", "\n", StringComparison.Ordinal), @"(?ms)^function [\w]+\.(\w+)\(([^)]*)\)\n(.*?)^end")
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => Flat(g.First().Groups[3].Value));

    /// <summary>A function body as one line, with the prefix written "s." and the passcode as id.</summary>
    private static string Flat(string body) =>
        Regex.Replace(Regex.Replace(string.Join(" ", body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))),
            @"\b(?:c\d+|s)\.", "s."), $@"\b{_sourceId}\b", "id");

    private static readonly Regex NegReg = new(
        @"^if chk==0 then return true end if not e:IsHasType\(EFFECT_TYPE_ACTIVATE\) then return end local c=e:GetHandler\(\) local e1=Effect\.CreateEffect\(c\) " +
        @"e1:SetType\(EFFECT_TYPE_FIELD\+EFFECT_TYPE_CONTINUOUS\) e1:SetCode\(EVENT_CHAIN_NEGATED\) e1:SetLabelObject\(e\) e1:SetOperation\(s\.(\w+)\) " +
        @"e1:SetReset\(RESET_CHAIN\) Duel\.RegisterEffect\(e1,tp\)$");

    private static readonly Regex NegCheck = new(
        @"^local te=e:GetLabelObject\(\) local de,dp=Duel\.GetChainInfo\(ev,CHAININFO_DISABLE_REASON,CHAININFO_DISABLE_PLAYER\) if rp==tp and de and dp==1-tp and re==te then " +
        @"local e1=Effect\.CreateEffect\(e:GetHandler\(\)\) e1:SetType\(EFFECT_TYPE_FIELD\+EFFECT_TYPE_CONTINUOUS\) e1:SetCode\(EVENT_CHAIN_END\) e1:SetOperation\(s\.(\w+)\) " +
        @"e1:SetLabelObject\(te\) Duel\.RegisterEffect\(e1,tp\) end$");

    private const string NegEvent = "local te=e:GetLabelObject() Duel.RaiseEvent(te:GetHandler(),EVENT_CUSTOM+id,te,0,tp,tp,0) e:Reset()";

    /// <summary>
    /// Cyber Emergency's negreg as an activation's cost: a watcher that raises EVENT_CUSTOM+id at the end of the Chain when
    /// the opponent negated the activation. The negated trigger block writes it, so it is no COST block.
    /// </summary>
    private static bool IsNegationWatch(string cost)
    {
        if (_sourceLua is not { } lua) return false;
        var fns = ScriptFunctions(lua);
        var name = Regex.Match(cost.Trim(), @"^[\w]+\.(\w+)$");
        if (!name.Success || !fns.TryGetValue(name.Groups[1].Value, out var body)) return false;
        var reg = NegReg.Match(body);
        if (!reg.Success || !fns.TryGetValue(reg.Groups[1].Value, out var check)) return false;
        var raise = NegCheck.Match(check);
        // The event is raised only there, so only the negated trigger can be listening.
        return raise.Success && fns.TryGetValue(raise.Groups[1].Value, out var ev) && ev == NegEvent
               && Regex.Matches(lua, @"Duel\.RaiseEvent\(").Count == 1;
    }

    /// <summary>The script's activation watches for its negation (<see cref="IsNegationWatch"/>), on an activation's cost.</summary>
    private static bool WatchesNegation() =>
        _sourceLua is { } lua && Regex.Matches(lua, @"\w+:SetCost\(([\w.]+)\)").Any(m => IsNegationWatch(m.Groups[1].Value));

    private const string NegatedCondition =
        "local de,dp=Duel.GetChainInfo(ev,CHAININFO_DISABLE_REASON,CHAININFO_DISABLE_PLAYER) return rp==tp and de and dp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE) " +
        "and e:GetHandler()==re:GetHandler() and e:GetHandler():GetReasonEffect()==de";

    private static readonly string[] SentByRulesCondition =
        ["local c=e:GetHandler() return c==re:GetHandler() and c:GetReasonEffect()==nil", "return e:GetHandler()==re:GetHandler() and e:GetHandler():GetReasonEffect()==nil"];

    /// <summary>
    /// Cyber Emergency's GY effect: a trigger in the GY on EVENT_CHAIN_NEGATED ("negated and destroyed by that effect") and
    /// its clone on EVENT_CUSTOM+id ("negated, then sent to the GY by the rules"), the rest of the effect shared. Null for
    /// other pieces.
    /// </summary>
    private static TranslatedPiece? NegatedPieceOf(ScriptPiece piece, List<Dictionary<string, string>> sets, Functions fns, CardData card)
    {
        if (sets.Count != 2) return null;
        var main = sets.FirstOrDefault(s => s.GetValueOrDefault("Code") == "EVENT_CHAIN_NEGATED");
        var custom = sets.FirstOrDefault(s => s.GetValueOrDefault("Code") is { } c && Regex.IsMatch(c, $@"^EVENT_CUSTOM\+(?:id|{piece.SourceId})$"));
        if (main is null || custom is null) return null;
        static IEnumerable<string> Rest(Dictionary<string, string> s) => s.Where(kv => kv.Key is not ("Code" or "Condition")).OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value);
        if (!Rest(main).SequenceEqual(Rest(custom))) throw No("its two negation triggers differ in more than their event");
        var type = Flags(main.GetValueOrDefault("Type") ?? "");
        var props = Flags(main.GetValueOrDefault("Property") ?? "");
        if (!(type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_O"]) || type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_F"]))
            || main.GetValueOrDefault("Range") != "LOCATION_GRAVE" || !props.Contains("EFFECT_FLAG_DELAY"))
            throw No("it reacts to a negation in a way the blocks don't");
        if (main.GetValueOrDefault("Condition") is not { } con || custom.GetValueOrDefault("Condition") is not { } con2
            || Flat(string.Join("\n", fns.Get(con).Body)) != NegatedCondition || !SentByRulesCondition.Contains(Flat(string.Join("\n", fns.Get(con2).Body))))
            throw No("it checks the negation in a way the blocks don't");
        if (!WatchesNegation()) throw No("it waits for a negation its activation doesn't watch for");

        // The rest of the effect is read as if it were used from the GY, then gets its trigger.
        var set = new Dictionary<string, string>(main) { ["Type"] = "EFFECT_TYPE_IGNITION" };
        set.Remove("Code");
        set.Remove("Condition");
        props.Remove("EFFECT_FLAG_DELAY");
        if (props.Count == 0) set.Remove("Property");
        else set["Property"] = string.Join("+", props);
        var read = TranslateSet(piece, set, Merge.None, fns, card);
        if (read.Effect is { } effect)
            effect.When = Block(NegatedActivationTrigger.BlockId).With("optional", type.Contains("EFFECT_TYPE_TRIGGER_O"));
        return read;
    }

    /// <summary>Where the effect being read is used from (its SetRange), for actions on this card that depend on it.</summary>
    [ThreadStatic] private static string? _effectRange;

    /// <summary>
    /// Cyber Emergency's "add this card to your hand", used from the GY or banishment: "if c:IsRelateToEffect(e) then
    /// Duel.SendtoHand(c,nil,REASON_EFFECT) end", with the target step checking it can go there, as the block does. Null
    /// for any other line.
    /// </summary>
    private static BlockInstance? SelfToHandAction(string line, TargetInfo target)
    {
        if (_effectRange is not ("LOCATION_GRAVE" or "LOCATION_REMOVED") || !Regex.IsMatch(line, @"^Duel\.SendtoHand\((?:c|e:GetHandler\(\)),nil,REASON_EFFECT\)$")) return null;
        if (!ChecksThisCardIsThere() || target.Check is not { } check || !Regex.IsMatch(check, @"(?:\bc|e:GetHandler\(\)):IsAbleToHand\(\)"))
            throw No("it adds this card to the hand without the checks the block makes");
        return Block("do.add_to_hand").With("cards", new CardFilter { ThisCard = true });
    }

    // ---------------------------------------------------------------- banish it, then return it to the field

    /// <summary>
    /// Interdimensional Matter Forwarder and Speedroid Clear Wing Wonder: "if tc:IsRelateToEffect(e) and
    /// Duel.Remove(tc,0,REASON_EFFECT+REASON_TEMPORARY)~=0 and not tc:IsReason(REASON_REDIRECT) and
    /// tc:IsLocation(LOCATION_REMOVED) then Duel.BreakEffect() Duel.ReturnToField(tc) end", read as one line the banish
    /// block takes: "Duel.CardCrafterBanishUntil(tc,return)" (or "return_monster" when it also checks the card is a monster).
    /// </summary>
    private static string BanishReturnRewrite(string lua) =>
        Regex.Replace(lua,
            @"(?m)^(\s*)if (\w+|e:GetHandler\(\)):IsRelateToEffect\(e\) and (\2:IsType\(TYPE_MONSTER\) and )?Duel\.Remove\(\2,0,REASON_EFFECT\+REASON_TEMPORARY\)~=0\s+and not \2:IsReason\(REASON_REDIRECT\) and \2:IsLocation\(LOCATION_REMOVED\) then\n\s*Duel\.BreakEffect\(\)\n\s*Duel\.ReturnToField\(\2\)\n(\s*)end$",
            m => $"{m.Groups[1].Value}if {m.Groups[2].Value}:IsRelateToEffect(e) then\n{m.Groups[1].Value}\tDuel.CardCrafterBanishUntil({m.Groups[2].Value},{(m.Groups[3].Success ? "return_monster" : "return")})\n{m.Groups[4].Value}end");

    /// <summary>"return_monster": the script checks the banished card is a monster, which a card in a Monster Zone always is.</summary>
    private static string BanishReturnUntil(string until, CardFilter cards) =>
        until != "return_monster" ? until
        : cards.ThisCard || cards.CardType == FilterCardType.Monster || cards.Zones == Model.Zone.MonsterZone ? "return"
        : throw No("it banishes and returns cards only if they are monsters, which the block doesn't check");

    // ---------------------------------------------------------------- locks

    /// <summary>"re:GetActivateLocation()==LOCATION_HAND or ...==LOCATION_GRAVE": a lock on activations in the hand or GY.</summary>
    private static bool IsHandOrGyLocation(string part) =>
        part is "(re:GetActivateLocation()==LOCATION_HAND or re:GetActivateLocation()==LOCATION_GRAVE)"
            or "(re:GetActivateLocation()==LOCATION_GRAVE or re:GetActivateLocation()==LOCATION_HAND)"
        || Regex.IsMatch(part, @"^re:GetActivateLocation\(\)&\(?LOCATION_(?:HAND[|+]LOCATION_GRAVE|GRAVE[|+]LOCATION_HAND)\)?(?:~=0|>0)$");
}
