using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Compiler;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "If your opponent Special Summoned a monster(s) this turn", "if a card was banished 3 or more times this turn", "if you
/// have not activated a card or effect this turn": any duel event, counted for the turn by a watcher (the game doesn't
/// remember them), from each player's point of view.
/// </summary>
public sealed class EventThisTurnCondition : ConditionBlock
{
    public const string BlockId = "if.event_this_turn";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "Something happened this turn (Summons, destroyed, banished, damage, activations...)";
    public override string Sentence => "{event} {did} this turn (at least {count} times)";
    public override string Help => "Counted from when the first copy of this card is in the Duel; events before that don't count.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "What", EventTrigger.Events.Select(e => new ChoiceOption(e.Key, e.Text)).ToArray()),
        ParamSpec.Choice("did", "Did it happen", new("did", "happened"), new("not", "did not happen")),
        ParamSpec.Int("count", "At least", 1, 1, 30),
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
        var past = Past(EventTrigger.Find(args.Choice("event")).Text);
        var n = args.Int("count");
        var times = n > 1 ? $" {n.ToString(CultureInfo.InvariantCulture)} or more times" : "";
        return args.Choice("did") == "not"
            ? (n > 1 ? $"it has not yet happened{times} this turn that {past}" : $"it has not happened this turn that {past}")
            : $"{past}{times} this turn";
    }

    public override void Compile(BlockContext ctx)
    {
        var key = ctx.Args.Choice("event");
        var index = Array.FindIndex(EventTrigger.Events, e => e.Key == key);
        var ev = EventTrigger.Find(key);
        var flag = $"id+{(8100 + Math.Max(0, index)).ToString(CultureInfo.InvariantCulture)}";
        var check = ev.Check;
        if (check is not null && check.IndexOf('@') is var at and >= 0)
        {
            var colon = check.IndexOf(':', at);
            var fn = ctx.Script.AddFunction(check[(at + 1)..colon], "c,tp", [$"return {check[(colon + 1)..]}"]);
            check = check[..at] + $"eg:IsExists({fn},1,nil,tp)";
        }
        var op = ctx.Script.AddFunction($"seen{Math.Max(0, index)}", "e,tp,eg,ep,ev,re,r,rp",
        [
            .. check is null ? Array.Empty<string>() : [$"if not ({check}) then return end"],
            $"Duel.RegisterFlagEffect(tp,{flag},RESET_PHASE+PHASE_END,0,1)",
        ]);
        // One watcher per player, so "you" and "your opponent" in the event mean each player in turn.
        var guard = $"s.cc_seen{Math.Max(0, index)}";
        ctx.Script.AddInitialLine(string.Join("\n\t",
        [
            $"if not {guard} then",
            $"\t{guard}=true",
            .. new[] { 0, 1 }.SelectMany(p => new[]
            {
                $"\tlocal sw{p}=Effect.CreateEffect(c)",
                $"\tsw{p}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
                $"\tsw{p}:SetCode({ev.Code})",
                $"\tsw{p}:SetOperation({op})",
                $"\tDuel.RegisterEffect(sw{p},{p})",
            }),
            "end",
        ]));
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        ctx.Effect.Conditions.Add(ctx.Args.Choice("did") == "not" ? $"Duel.GetFlagEffect(tp,{flag})<{n}" : $"Duel.GetFlagEffect(tp,{flag})>={n}");
    }
}
