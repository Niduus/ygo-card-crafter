using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Compiler;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "If your opponent Special Summoned a monster(s) this turn", "if a card was banished 3 or more times this turn", "if you
/// have not activated a card or effect this turn", "if your opponent Normal or Special Summoned 5 or more monsters this
/// turn": any duel event, counted for the turn by a watcher (the game doesn't remember them), from each player's point of
/// view. It counts the times it happened or the cards it happened to; from the start of the Duel, or only while this card
/// is face-up (Invoked Caliga).
/// </summary>
public sealed class EventThisTurnCondition : ConditionBlock
{
    public const string BlockId = "if.event_this_turn";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "Something happened this turn (Summons, destroyed, banished, damage, activations...)";
    public override string Sentence => "{event} {did} this turn (at least {count} {counting}) {watch}";
    public override string Help => "Counted from when the first copy of this card is in the Duel; events before that don't count. " +
                                   "\"Only while this card is face-up\" counts what happens while it is on the field, from its controller's point of view.";

    /// <summary>
    /// Events only this block has: both kinds of Summon at once ("Normal or Special Summoned 5 or more monsters"). The
    /// WHEN block says these with "Or when" instead.
    /// </summary>
    public static readonly EventTrigger.DuelEvent[] Extra =
    [
        new("opp_normal_special", "your opponent Normal or Special Summons a monster(s)", "EVENT_SUMMON_SUCCESS+EVENT_SPSUMMON_SUCCESS", "eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)"),
        new("you_normal_special", "you Normal or Special Summon a monster(s)", "EVENT_SUMMON_SUCCESS+EVENT_SPSUMMON_SUCCESS", "eg:IsExists(Card.IsSummonPlayer,1,nil,tp)"),
        new("any_normal_special", "a monster(s) is Normal or Special Summoned", "EVENT_SUMMON_SUCCESS+EVENT_SPSUMMON_SUCCESS"),
    ];

    private static IEnumerable<EventTrigger.DuelEvent> All => EventTrigger.Events.Concat(Extra);

    public static EventTrigger.DuelEvent Find(string key) => All.FirstOrDefault(e => e.Key == key) ?? EventTrigger.Events[0];

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "What", All.Select(e => new ChoiceOption(e.Key, e.Text)).ToArray()),
        ParamSpec.Choice("did", "Did it happen", new("did", "happened"), new("not", "did not happen")),
        ParamSpec.Int("count", "At least", 1, 1, 30),
        ParamSpec.Choice("counting", "Counting", new("times", "times", "times it happened"), new("cards", "cards", "cards it happened to (monsters Summoned...)")),
        ParamSpec.Choice("watch", "Counted", new("duel", "", "from the start of the Duel"), new("face_up", "", "only while this card is face-up")),
    ];

    private static readonly (string Present, string Past)[] Verbs =
    [
        ("activates", "activated"), ("activate", "activated"), ("Special Summons", "Special Summoned"), ("Special Summon", "Special Summoned"),
        ("Normal Summons", "Normal Summoned"), ("Normal Summon", "Normal Summoned"), ("Flip Summons", "Flip Summoned"), ("Flip Summon", "Flip Summoned"),
        ("takes", "took"), ("take", "took"), ("gains", "gained"), ("gain", "gained"), ("pays", "paid"), ("pay", "paid"), ("draws", "drew"), ("draw", "drew"),
        ("adds", "added"), ("add", "added"), ("declares", "declared"), ("Sets", "Set"), ("leaves", "left"), ("changes", "changed"), ("is", "was"),
    ];

    /// <summary>"your opponent Special Summons a monster(s)" as "your opponent Special Summoned a monster(s)".</summary>
    public static string Past(string text)
    {
        foreach (var (present, past) in Verbs)
        {
            var m = Regex.Match(text, $@"\b{Regex.Escape(present)}\b");
            if (m.Success) return text[..m.Index] + past + text[(m.Index + m.Length)..];
        }
        return text;
    }

    public override string Describe(BlockArgs args)
    {
        var past = Past(Find(args.Choice("event")).Text);
        var n = args.Int("count");
        var count = n.ToString(CultureInfo.InvariantCulture);
        // "your opponent Normal or Special Summoned 5 or more monsters this turn": the cards are counted.
        if (args.Choice("counting") == "cards" && n > 1 && Regex.Match(past, @"\ba (monster|card|Spell/Trap Card)\(s\)") is { Success: true } noun)
        {
            var many = past[..noun.Index] + (args.Choice("did") == "not" ? $"{count} {noun.Groups[1].Value}s" : $"{count} or more {noun.Groups[1].Value}s") + past[(noun.Index + noun.Length)..];
            return args.Choice("did") == "not" ? $"it has not yet happened this turn that {many}" : $"{many} this turn";
        }
        var times = n > 1 ? $" {count} or more times" : "";
        return args.Choice("did") == "not"
            ? (n > 1 ? $"it has not yet happened{times} this turn that {past}" : $"it has not happened this turn that {past}")
            : $"{past}{times} this turn";
    }

    /// <summary>How many cards of the event count, from its check: "eg:IsExists(f,1,nil,x)" counts as "eg:FilterCount(f,nil,x)".</summary>
    private static (string? Guard, string Count)? CardCount(string? check)
    {
        if (check is null) return (null, "eg:GetCount()");
        var m = Regex.Match(check, @"^(?:(.+) and )?eg:IsExists\(([\w.]+),1,nil,([^()]*)\)$");
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : null, $"eg:FilterCount({m.Groups[2].Value},nil,{m.Groups[3].Value})") : null;
    }

    public override void Compile(BlockContext ctx)
    {
        var key = ctx.Args.Choice("event");
        var index = Math.Max(0, All.ToList().FindIndex(e => e.Key == key));
        var ev = Find(key);
        var cards = ctx.Args.Choice("counting") == "cards";
        var faceUp = ctx.Args.Choice("watch") == "face_up";
        // The player's flags for the turn (8100, cards 8500) or this card's while it is face-up (8600, cards 8700).
        var flag = $"id+{((faceUp ? (cards ? 8700 : 8600) : (cards ? 8500 : 8100)) + index).ToString(CultureInfo.InvariantCulture)}";
        var check = ev.Check;
        if (check is not null && check.IndexOf('@') is var at and >= 0)
        {
            var colon = check.IndexOf(':', at);
            var fn = ctx.Script.AddFunction(check[(at + 1)..colon], "c,tp", [$"return {check[(colon + 1)..]}"]);
            check = check[..at] + $"eg:IsExists({fn},1,nil,tp)";
        }
        // A flag for the turn on the player, or on this card while it stays face-up.
        var register = faceUp
            ? $"e:GetHandler():RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,0,1)"
            : $"Duel.RegisterFlagEffect(tp,{flag},RESET_PHASE+PHASE_END,0,1)";
        List<string> body;
        if (cards)
        {
            if (CardCount(check) is not { } counted)
            {
                ctx.Effect.Error($"\"{Name}\": \"{ev.Text}\" happens to no cards that can be counted. Count the times it happened instead.");
                return;
            }
            body = [.. counted.Guard is null ? Array.Empty<string>() : [$"if not ({counted.Guard}) then return end"], $"for i=1,{counted.Count} do {register} end"];
        }
        else body = [.. check is null ? Array.Empty<string>() : [$"if not ({check}) then return end"], register];
        var op = ctx.Script.AddFunction($"seen{index}", "e,tp,eg,ep,ev,re,r,rp", body);
        var codes = ev.Code.Split('+');
        if (faceUp)
        {
            // While it is face-up on the field, from its controller's point of view (Invoked Caliga).
            var range = Zones.Resolve("auto", ctx.Card);
            ctx.Script.AddInitialLine(string.Join("\n\t", codes.SelectMany((code, i) => new[]
            {
                $"local fw{i}=Effect.CreateEffect(c)",
                $"fw{i}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
                $"fw{i}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
                $"fw{i}:SetCode({code})",
                $"fw{i}:SetRange({range})",
                $"fw{i}:SetOperation({op})",
                $"c:RegisterEffect(fw{i})",
            })));
        }
        else
        {
            // One watcher per player, so "you" and "your opponent" in the event mean each player in turn.
            var guard = $"s.cc_seen{(cards ? "c" : "")}{index}";
            ctx.Script.AddInitialLine(string.Join("\n\t",
            [
                $"if not {guard} then",
                $"\t{guard}=true",
                .. new[] { 0, 1 }.SelectMany(p => codes.SelectMany((code, i) => new[]
                {
                    $"\tlocal sw{p}{(i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture))}=Effect.CreateEffect(c)",
                    $"\tsw{p}{(i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture))}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
                    $"\tsw{p}{(i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture))}:SetCode({code})",
                    $"\tsw{p}{(i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture))}:SetOperation({op})",
                    $"\tDuel.RegisterEffect(sw{p}{(i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture))},{p})",
                })),
                "end",
            ]));
        }
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        var count = faceUp ? $"e:GetHandler():GetFlagEffect({flag})" : $"Duel.GetFlagEffect(tp,{flag})";
        ctx.Effect.Conditions.Add(ctx.Args.Choice("did") == "not" ? $"{count}<{n}" : $"{count}>={n}");
    }
}
