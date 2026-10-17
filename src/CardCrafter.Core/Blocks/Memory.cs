using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// What a card remembers for its later effects: cards ("the destroyed monster", "the banished card") or a number ("the
/// LP paid", "how many times this effect resolved"), each in one of five slots, Memory A to E. Kept the way official
/// scripts keep them, as flag effects: a remembered card carries the slot's flag (reset when it leaves where it is, as
/// RESETS_STANDARD does, and when the memory's time is up); a number is the label of a flag on this card, and a count
/// is how many flags it carries. Cards remembered by any copy of this card are remembered, as cards flagged with a
/// script's id are; a number belongs to the copy that remembered it.
/// </summary>
public static class Memory
{
    public const string SlotKey = "slot";

    /// <summary>The slots. Card text never names them: "Memory A" is only how the editor tells them apart.</summary>
    public static readonly ChoiceOption[] Slots =
        "ABCDE".Select(s => new ChoiceOption(s.ToString(), "", "Memory " + s)).ToArray();

    public static int Index(string slot) => Math.Max(0, Array.FindIndex(Slots, s => s.Value == slot));

    /// <summary>The Memory blank: the editor says what the card's other blocks remember in each one.</summary>
    public static ParamSpec SlotParam(string key = SlotKey) =>
        new() { Key = key, Label = "Memory", Type = ParamType.Choice, Default = Slots[0].Value, Choices = Slots, EditorChoices = Offered };

    /// <summary>"Memory A: the destroyed monster (effect 1)", "Memory B (not used yet)": the slots as the card uses them.</summary>
    public static IReadOnlyList<ChoiceOption> Offered(CardProject project) => Slots.Select(s =>
    {
        var uses = Writers(project, s.Value)
            .Where(w => w.Block.Values.GetValueOrDefault("what") != "forget")
            .Select(w =>
            {
                var def = BlockRegistry.Find(RememberAction.BlockId)!;
                var text = def.Describe(new BlockArgs(def, w.Block) { Effect = w.Effect, CardName = project.Card.Name });
                if (text.StartsWith("remember ", StringComparison.Ordinal)) text = text["remember ".Length..];
                if (text.Length == 0) text = "a count";
                return $"{text} (effect {project.Effects.IndexOf(w.Effect) + 1})";
            })
            .Distinct().ToList();
        return new ChoiceOption(s.Value, s.Label, uses.Count == 0 ? $"Memory {s.Value} (nothing remembered in it yet)" : $"Memory {s.Value}: {string.Join("; ", uses)}");
    }).ToList();

    /// <summary>The slot's flag code: on the remembered cards, or on this card for a number.</summary>
    public static string Flag(string slot) => $"id+{(9100 + Index(slot)).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Where remembered cards can be: everywhere but under an Xyz Monster (becoming material resets their flags).</summary>
    public const string Everywhere = "LOCATION_DECK+LOCATION_HAND+LOCATION_ONFIELD+LOCATION_GRAVE+LOCATION_REMOVED+LOCATION_EXTRA";

    public static readonly ChoiceOption[] Durations =
    [
        new("turn", "until the end of this turn"),
        new("chain", "", "until the end of this Chain"),
        new("next", "until the end of the next turn"),
        new("face_up", "while this card is face-up", "while this card is face-up on the field (numbers)"),
        new("duel", "for the rest of this Duel", "for the rest of the Duel"),
    ];

    /// <summary>
    /// The reset of a remembered number's flag on this card: it lasts wherever this card goes, except "while face-up",
    /// which ends when this card leaves the field or is turned face-down.
    /// </summary>
    public static (string Reset, int Count) NumberReset(string duration) => duration switch
    {
        "chain" => ("RESET_CHAIN", 1),
        "next" => ("RESET_PHASE+PHASE_END", 2),
        "face_up" => ("RESET_EVENT+RESETS_STANDARD", 1),
        "duel" => ("0", 1),
        _ => ("RESET_PHASE+PHASE_END", 1),
    };

    /// <summary>The reset of a remembered card's flag: it is forgotten when it leaves where it is, as official flags are.</summary>
    public static (string Reset, int Count) CardReset(string duration) => duration switch
    {
        "chain" => ("RESET_EVENT+RESETS_STANDARD+RESET_CHAIN", 1),
        "next" => ("RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END", 2),
        "duel" => ("RESET_EVENT+RESETS_STANDARD", 1),
        _ => ("RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END", 1),
    };

    /// <summary>What a remember block keeps: "cards", "number" (a value or a count), or "forget" (either).</summary>
    public static string KindOf(string what) => what switch
    {
        "affected" or "target" or "event" or "battle" => "cards",
        "forget" => "forget",
        _ => "number",
    };

    /// <summary>Every remember block of the card that uses <paramref name="slot"/>, with the effect it is in.</summary>
    public static IEnumerable<(BlockInstance Block, EffectEvent Effect)> Writers(CardProject project, string slot) =>
        project.Effects.SelectMany(e => e.Do.Where(b => b.BlockId == RememberAction.BlockId && Slot(b) == slot).Select(b => (b, e)));

    public static string Slot(BlockInstance b) => b.Values.GetValueOrDefault(SlotKey, "A") is var s && Slots.Any(o => o.Value == s) ? s : "A";

    /// <summary>
    /// The kind of memory <paramref name="slot"/> is, from the blocks that remember into it: "cards" or "number", or null
    /// when none does (or only forgets).
    /// </summary>
    public static string? KindIn(CardProject project, string slot) =>
        Writers(project, slot).Select(w => KindOf(w.Block.Values.GetValueOrDefault("what", "affected"))).FirstOrDefault(k => k != "forget");

    /// <summary>Checks a reader of the slot: some block remembers into it, and the same kind of thing.</summary>
    public static void CheckRead(BlockContext ctx, string slot, string kind)
    {
        var name = "Memory " + slot;
        if (KindIn(ctx.Script.Project, slot) is not { } found)
            ctx.Effect.Warn($"Nothing on this card remembers anything as {name}, so it is always empty. Add a \"Remember\" block.");
        else if (found != kind)
            ctx.Effect.Error($"{name} holds {(found == "cards" ? "cards" : "a number")}, but \"{ctx.Args.Definition.Name}\" reads {(kind == "cards" ? "cards" : "a number")} from it.");
    }

    /// <summary>"s.memnum(e:GetHandler(),id+9100)": the number this card remembers, the total of its flags' labels.</summary>
    public static string NumberLua(ScriptBuilder script, string slot)
    {
        var fn = script.AddFunction("memnum", "c,f",
        [
            "local n=0",
            "for _,v in ipairs({c:GetFlagEffectLabel(f)}) do n=n+v end",
            "return n",
        ]);
        return $"{fn}(e:GetHandler(),{Flag(slot)})";
    }

    /// <summary>The filter of remembered cards: "s.memcard(c,f)".</summary>
    public static string CardFilter(ScriptBuilder script) => script.AddFunction("memcard", "c,f", ["return c:GetFlagEffect(f)>0"]);

    /// <summary>"s.memgroup(id+9100)": every card remembered in the slot, wherever it is.</summary>
    public static string GroupLua(ScriptBuilder script, string slot)
    {
        var fn = script.AddFunction("memgroup", "f", [$"return Duel.GetMatchingGroup({CardFilter(script)},0,{Everywhere},{Everywhere},nil,f)"]);
        return $"{fn}({Flag(slot)})";
    }

    /// <summary>A total of the remembered cards' stats (never below 0, as "?" ATK counts as 0): "s.memstat(id+9100,Card.GetAttack)".</summary>
    public static string StatLua(ScriptBuilder script, string slot, string getter)
    {
        var group = script.AddFunction("memgroup", "f", [$"return Duel.GetMatchingGroup({CardFilter(script)},0,{Everywhere},{Everywhere},nil,f)"]);
        var fn = script.AddFunction("memstat", "f,stat",
        [
            "local n=0",
            $"for tc in aux.Next({group}(f)) do n=n+math.max(0,stat(tc)) end",
            "return n",
        ]);
        return $"{fn}({Flag(slot)},{getter})";
    }

    /// <summary>The picker requirement "that was remembered": its Lua for one slot, used by <see cref="CardProperties"/>.</summary>
    public static string RequirementLua(int index) => $"c:GetFlagEffect(id+{(9100 + index).ToString(CultureInfo.InvariantCulture)})>0";

    /// <summary>
    /// "the destroyed monster", "the cards sent to the GY", "the Special Summoned monsters": the cards the DO block before
    /// <paramref name="args"/>' block affected, as card text names them.
    /// </summary>
    public static string Affected(BlockArgs args)
    {
        var dos = args.Effect?.Do;
        var at = dos?.FindIndex(b => ReferenceEquals(b, args.Instance)) ?? -1;
        if (at <= 0 || BlockRegistry.Find(dos![at - 1].BlockId) is not { } def) return "the affected card";
        var prev = new BlockArgs(def, dos[at - 1], args.Pack) { Effect = args.Effect, CardName = args.CardName };
        var monster = def.Param("cards") is { Type: ParamType.Cards } && prev.Cards("cards") is var f
            && (f.CardType == FilterCardType.Monster || f.Zones == Zone.MonsterZone || def.Id is "do.special_summon" or "do.tribute");
        var self = prev.IsThisCard || def.Id == "do.special_summon_self";
        var plural = !self && prev.IsPlural;
        var noun = self ? "this card" : (monster ? "monster" : "card") + (plural ? "s" : "");
        if (self) return noun;
        var (before, after) = def.Id switch
        {
            "do.destroy" => ("destroyed ", ""),
            "do.banish" => ("banished ", ""),
            "do.special_summon" => ("Summoned ", ""),
            "do.tribute" => ("Tributed ", ""),
            "do.discard" => ("discarded ", ""),
            "do.send_to_gy" or "do.mill" => ("", " sent to the GY"),
            "do.return_to_hand" => ("", " returned to the hand"),
            "do.add_to_hand" => ("", " added to the hand"),
            "do.shuffle_into_deck" => ("", " shuffled into the Deck"),
            _ => ("affected ", ""),
        };
        return $"the {before}{noun}{after}";
    }

    /// <summary>"the target", "those targets": this effect's target(s), from the block that targets.</summary>
    public static string Targets(BlockArgs args)
    {
        var targeting = args.Effect?.Do.FirstOrDefault(b => b.Values.GetValueOrDefault("target") == "true");
        var plural = targeting is not null && BlockRegistry.Find(targeting.BlockId) is { } def && new BlockArgs(def, targeting, args.Pack).IsPlural;
        return plural ? "the targets" : "the target";
    }

    /// <summary>Whether the effect targets with one of its blocks, so CHAININFO_TARGET_CARDS holds its targets.</summary>
    public static bool Targets(EffectBuilder e) => e.Properties.Contains("EFFECT_FLAG_CARD_TARGET");

    /// <summary>
    /// The Lua group of the cards a remember or delayed block is about ("affected", "target", "event", "battle", or a
    /// slot's remembered cards), or null after an error.
    /// </summary>
    public static string? GroupOf(BlockContext ctx, string what, string slot)
    {
        var e = ctx.Effect;
        switch (what)
        {
            case "affected":
                if (e.LastResult is null) { e.Error($"\"{ctx.Args.Definition.Name}\": \"the cards the step before affected\" needs a DO block before it that moves, destroys or Summons cards."); return null; }
                return "Duel.GetOperatedGroup():Clone()";
            case "target":
                if (!Targets(e)) { e.Error($"\"{ctx.Args.Definition.Name}\": \"this effect's target\" needs a DO block of this effect that targets."); return null; }
                return "Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(Card.IsRelateToEffect,nil,e)";
            case "event":
                if (e.Code is null or "EVENT_FREE_CHAIN" || e.IsPassive)
                    e.Warn($"\"{ctx.Args.Definition.Name}\": \"the cards that started this effect\" needs a WHEN about cards, like \"If a monster is destroyed\"; otherwise there are none.");
                return "(eg and eg:Clone() or Group.CreateGroup())";
            case "battle":
                return "Group.FromCards(c:GetBattleTarget())";
            default:
                return GroupLua(ctx.Script, slot);
        }
    }
}

/// <summary>
/// "Remember the destroyed monster until the end of this turn", "remember the LP paid while this card is face-up", "count
/// this resolution for the rest of this Duel": keeps cards or a number in a slot for the blocks of this card's other
/// effects (or later steps of this one) to use.
/// </summary>
public sealed class RememberAction : ActionBlock
{
    public const string BlockId = "do.remember";
    public override string Id => BlockId;
    public override string Group => "Memory";
    public override string Name => "Remember cards or a number (Memory A-E) for later";
    public override string Sentence => "Remember {what} {n} {how} {amount} {value} {mem} {counter} {counter_id} {of} as {slot} {duration}";
    public override string Help =>
        "Cards are remembered until they leave where they are (as official cards' flags work), or until the time you pick is up. " +
        "A number belongs to this copy of the card; \"add to the count\" adds to it each time, as \"the number of times this effect resolved\" counts. " +
        "Later, use \"that was remembered\" in a card picker, \"the remembered number\" or \"the remembered monster's ATK\" as an amount, " +
        "the IF block \"Memory ...\", or \"... during the next Standby Phase\".";

    private static string What(BlockInstance b) => b.Values.GetValueOrDefault("what", "affected");

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "What",
            new("affected", "", "the card(s) the step before affected (the destroyed monster...)"),
            new("target", "", "this effect's target(s)"),
            new("event", "", "the card(s) that started this effect"),
            new("battle", "", "the monster this card is battling"),
            new("value", "", "a number:"),
            new("count", "", "how many cards the step before affected"),
            new("affected_atk", "", "the ATK the affected monster had on the field"),
            new("affected_orig_atk", "", "the affected monster's original ATK"),
            new("add", "", "a count, adding this many:"),
            new("forget", "", "nothing: forget what it holds")),
        ParamSpec.Int("n", "Add", 1, 1, 99).ShowIf(b => What(b) == "add"),
        .. ValueSources.Params().Select(p =>
        {
            var inner = p.ShownWhen;
            return p.ShowIf(b => What(b) == "value" && (inner?.Invoke(b) ?? true));
        }),
        Memory.SlotParam(),
        ParamSpec.Choice("duration", "For how long", Memory.Durations).ShowIf(b => What(b) != "forget"),
    ];

    private static string Duration(BlockArgs args) => args.Describe("duration") is { Length: > 0 } d ? " " + d : "";

    public override string Describe(BlockArgs args) => args.Choice("what") switch
    {
        "affected" => $"remember {Memory.Affected(args)}{Duration(args)}",
        "target" => $"remember {Memory.Targets(args)}{Duration(args)}",
        "event" => $"remember that card(s){Duration(args)}",
        "battle" => $"remember the monster this card battled{Duration(args)}",
        "value" => $"remember {ValueSources.Phrase(args, "", "this card", forEach: false)["equal to ".Length..]}{Duration(args)}",
        "count" => $"remember the number of {Plural(Memory.Affected(args))}{Duration(args)}",
        "affected_atk" => $"remember the ATK {Memory.Affected(args)} had on the field{Duration(args)}",
        "affected_orig_atk" => $"remember the original ATK of {Memory.Affected(args)}{Duration(args)}",
        // A count says itself in the IF block that reads it ("If this is the third time this effect has resolved").
        _ => "",
    };

    /// <summary>"the destroyed monster" as "destroyed monsters" for "the number of ...".</summary>
    private static string Plural(string the)
    {
        var s = the.StartsWith("the ", StringComparison.Ordinal) ? the[4..] : the;
        foreach (var noun in new[] { "monster", "card" })
            if (s.Contains(noun + " ", StringComparison.Ordinal) || s.EndsWith(noun, StringComparison.Ordinal))
            {
                var at = s.IndexOf(noun, StringComparison.Ordinal);
                if (at + noun.Length < s.Length && s[at + noun.Length] == 's') return s;
                return s[..(at + noun.Length)] + "s" + s[(at + noun.Length)..];
            }
        return s;
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var what = ctx.Args.Choice("what");
        var slot = ctx.Args.Choice(Memory.SlotKey);
        var duration = ctx.Args.Choice("duration");
        var flag = Memory.Flag(slot);
        var kind = Memory.KindOf(what);
        var others = Memory.Writers(ctx.Script.Project, slot).Select(w => Memory.KindOf(w.Block.Values.GetValueOrDefault("what", "affected"))).Where(k => k != "forget").Distinct().ToList();
        if (others.Count > 1) { e.Error($"Memory {slot} is used for cards by one block and for a number by another. Use another Memory for one of them."); return; }
        if (kind == "cards" && duration == "face_up")
        {
            e.Error("Remembered cards are forgotten when they leave where they are, not when this card does: pick another time, or remember a number instead.");
            return;
        }
        var r = e.NewLocal("r");
        var lines = new List<string> { $"local {r}=0" };
        if (kind == "cards")
        {
            if (Memory.GroupOf(ctx, what, slot) is not { } group) return;
            var (reset, count) = Memory.CardReset(duration);
            var g = e.NewLocal("mg");
            lines.Add(what == "battle"
                ? $"local {g}=c:GetBattleTarget() and Group.FromCards(c:GetBattleTarget()) or Group.CreateGroup()"
                : $"local {g}={group}");
            lines.Add($"for tc in aux.Next({g}) do tc:RegisterFlagEffect({flag},{reset},0,{count.ToString(CultureInfo.InvariantCulture)}) {r}={r}+1 end");
            e.AddStep(lines, r, ctx.Link);
            return;
        }
        if (what == "forget")
        {
            var whole = Memory.KindIn(ctx.Script.Project, slot);
            lines.Add(whole == "cards"
                ? $"for tc in aux.Next({Memory.GroupLua(ctx.Script, slot)}) do tc:ResetFlagEffect({flag}) {r}={r}+1 end"
                : $"c:ResetFlagEffect({flag}) {r}=1");
            e.AddStep(lines, r, ctx.Link);
            return;
        }
        string? value = what switch
        {
            "value" => ValueSources.Lua(ctx, "c", "tp"),
            "count" => e.LastResult ?? Missing(ctx),
            "affected_atk" or "affected_orig_atk" => OperatedTotal(ctx, what == "affected_atk" ? "GetPreviousAttackOnField" : "GetTextAttack"),
            _ => ctx.Args.Int("n").ToString(CultureInfo.InvariantCulture),
        };
        if (value is null) return;
        var (nreset, ncount) = Memory.NumberReset(duration);
        var faceUp = duration == "face_up";
        if (faceUp && ctx.Effect.Range is not ("LOCATION_MZONE" or "LOCATION_SZONE" or "LOCATION_ONFIELD" or "LOCATION_FZONE" or "LOCATION_PZONE" or null))
            e.Warn($"\"While this card is face-up\": this effect is used where this card isn't on the field, so it remembers only if this card is face-up on the field when it resolves.");
        var ind = faceUp ? "\t" : "";
        if (faceUp) lines.Add("if c:IsFaceup() and c:IsOnField() and c:IsRelateToEffect(e) then");
        // A value takes the slot's place; a count adds a flag each time, as official cards count uses.
        if (what != "add") lines.Add($"{ind}c:ResetFlagEffect({flag})");
        lines.Add($"{ind}c:RegisterFlagEffect({flag},{nreset},0,{ncount.ToString(CultureInfo.InvariantCulture)},{value})");
        lines.Add($"{ind}{r}=1");
        if (faceUp) lines.Add("end");
        e.AddStep(lines, r, ctx.Link);
    }

    private static string? Missing(BlockContext ctx)
    {
        ctx.Effect.Error("\"How many cards the step before affected\" needs a DO block before it that affects cards.");
        return null;
    }

    /// <summary>The total of a stat of the cards the step before affected (never below 0), as one expression.</summary>
    private static string? OperatedTotal(BlockContext ctx, string getter)
    {
        if (ctx.Effect.LastResult is null) return Missing(ctx);
        var fn = ctx.Script.AddFunction("memtotal", "g,stat",
        [
            "local n=0",
            "for tc in aux.Next(g) do n=n+math.max(0,stat(tc)) end",
            "return n",
        ]);
        return $"{fn}(Duel.GetOperatedGroup(),Card.{getter})";
    }
}

/// <summary>
/// "If a remembered card is still there", "if this is the third time this effect has resolved this Duel", "if the
/// remembered number is 3000 or more": a test of a slot, as an IF block (or among the DO blocks, for "then if ...").
/// </summary>
public sealed class MemoryCondition : ConditionBlock
{
    public const string BlockId = "if.memory";
    public override string Id => BlockId;
    public override string Group => "Memory";
    public override string Name => "What this card remembers (Memory A-E): holds a card, a number of at least...";
    public override string Sentence => "{slot} {test} {n}";
    public override string Help => "Reads what \"Remember\" blocks of this card keep. Among the DO blocks, after \"Remember ... adding 1\", " +
                                   "\"is exactly 3\" reads \"If this is the third time this effect has resolved\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        Memory.SlotParam(),
        ParamSpec.Choice("test", "Test",
            new("holds", "", "holds a card that is still there"),
            new("empty", "", "holds no card that is still there"),
            new("ge", "", "is a number at least"),
            new("eq", "", "is a number exactly"),
            new("le", "", "is a number at most"),
            new("gt", "", "is a number more than")),
        ParamSpec.Int("n", "Number", 3, 0, 999999).ShowIf(b => b.Values.GetValueOrDefault("test", "holds") is not ("holds" or "empty")),
    ];

    private static readonly string[] Ordinals = ["zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"];

    /// <summary>The block that counts the times this effect resolved into the slot, when this effect does it before the test.</summary>
    private static BlockInstance? OwnCount(BlockArgs args)
    {
        var dos = args.Effect?.Do;
        if (dos is null) return null;
        var at = dos.FindIndex(b => ReferenceEquals(b, args.Instance));
        var slot = args.Choice(Memory.SlotKey);
        var counted = dos.Take(at < 0 ? 0 : at).LastOrDefault(b => b.BlockId == RememberAction.BlockId && Memory.Slot(b) == slot);
        return counted is not null && counted.Values.GetValueOrDefault("what") == "add" && counted.Values.GetValueOrDefault("n", "1") == "1" ? counted : null;
    }

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("n");
        var num = n.ToString(CultureInfo.InvariantCulture);
        var test = args.Choice("test");
        if (test == "holds") return "a remembered card is still there";
        if (test == "empty") return "no remembered card is still there";
        // "If this is the third time this effect has resolved this Duel": a count of this effect's own resolutions.
        if (OwnCount(args) is { } count && count.Values.GetValueOrDefault("duration", "turn") is "turn" or "duel" or "face_up" && n >= 1)
        {
            var when = count.Values.GetValueOrDefault("duration", "turn") switch { "duel" => " this Duel", "face_up" => " while this card has been face-up", _ => " this turn" };
            return test switch
            {
                "eq" => $"this is the {(n < Ordinals.Length ? Ordinals[n] : num + "th")} time this effect has resolved{when}",
                "ge" => $"this effect has resolved {num} or more times{when}",
                "gt" => $"this effect has resolved more than {num} times{when}",
                _ => $"this effect has resolved {num} or fewer times{when}",
            };
        }
        return test switch
        {
            "eq" => $"the remembered number is exactly {num}",
            "le" => $"the remembered number is {num} or less",
            "gt" => $"the remembered number is more than {num}",
            _ => $"the remembered number is {num} or more",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var slot = ctx.Args.Choice(Memory.SlotKey);
        var test = ctx.Args.Choice("test");
        var n = ctx.Args.Int("n").ToString(CultureInfo.InvariantCulture);
        if (test is "holds" or "empty")
        {
            Memory.CheckRead(ctx, slot, "cards");
            var exists = $"Duel.IsExistingMatchingCard({Memory.CardFilter(ctx.Script)},tp,{Memory.Everywhere},{Memory.Everywhere},1,nil,{Memory.Flag(slot)})";
            ctx.Effect.Conditions.Add(test == "holds" ? exists : "not " + exists);
            return;
        }
        Memory.CheckRead(ctx, slot, "number");
        var op = test switch { "eq" => "==", "le" => "<=", "gt" => ">", _ => ">=" };
        ctx.Effect.Conditions.Add($"{Memory.NumberLua(ctx.Script, slot)}{op}{n}");
    }
}

/// <summary>
/// "Special Summon it during your next Standby Phase", "return them to the hand at the end of the Battle Phase", "destroy
/// the remembered monster during the End Phase of the next turn": a delayed step on the cards the step before affected,
/// this effect's target(s), or a slot's remembered cards. They are marked with this card's field id, and only cards still
/// marked (the same cards, still where they were put) are affected, as official delayed effects do it.
/// </summary>
public sealed class LaterAction : ActionBlock
{
    public const string BlockId = "do.later";
    public override string Id => BlockId;
    public override string Group => "Memory";
    public override string Name => "Later: Special Summon, return, destroy... it during the next Standby Phase / End Phase";
    public override string Sentence => "{how} {what} {slot} {when}";
    public override string Help => "Cards that leave where they are before then (or are no longer the same cards) are not affected. To bring a " +
                                   "banished card back, use \"Banish cards\" with \"until the next Standby Phase\".";

    public static readonly ChoiceOption[] Whens =
    [
        new("standby_your", "during your next Standby Phase"),
        new("standby_next", "during the next Standby Phase"),
        new("standby_opp", "during your opponent's next Standby Phase"),
        new("end", "during the End Phase"),
        new("end_next", "during the End Phase of the next turn"),
        new("battle_end", "at the end of the Battle Phase", "at the end of this turn's Battle Phase"),
    ];

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "What happens",
            new("special", "Special Summon"),
            new("hand", "return to the hand:"),
            new("destroy", "destroy"),
            new("banish", "banish"),
            new("grave", "send to the GY:"),
            new("deck", "shuffle into the Deck:")),
        ParamSpec.Choice("what", "Which cards",
            new("affected", "", "the card(s) the step before affected"),
            new("target", "", "this effect's target(s)"),
            new("memory", "", "the remembered cards in")),
        Memory.SlotParam().ShowIf(b => b.Values.GetValueOrDefault("what") == "memory"),
        ParamSpec.Choice("when", "When", Whens),
    ];

    /// <summary>"it" or "them" for the cards of the step before, "the target", or "the remembered cards".</summary>
    private static string Object(BlockArgs args)
    {
        switch (args.Choice("what"))
        {
            case "target": return Memory.Targets(args) == "the targets" ? "them" : "it";
            case "memory": return "the remembered cards";
        }
        var dos = args.Effect?.Do;
        var at = dos?.FindIndex(b => ReferenceEquals(b, args.Instance)) ?? -1;
        if (at <= 0 || BlockRegistry.Find(dos![at - 1].BlockId) is not { } def) return "it";
        return new BlockArgs(def, dos[at - 1], args.Pack).IsPlural ? "them" : "it";
    }

    public override string Describe(BlockArgs args)
    {
        var it = Object(args);
        var when = args.Describe("when");
        return args.Choice("how") switch
        {
            "special" => $"Special Summon {it} {when}",
            "hand" => $"return {it} to the hand {when}",
            "banish" => $"banish {it} {when}",
            "grave" => $"send {it} to the GY {when}",
            "deck" => $"shuffle {it} into the Deck {when}",
            _ => $"destroy {it} {when}",
        };
    }

    /// <summary>
    /// The delayed effect's timing: its code, the lines that set its reset and labels (fid first), and the test its
    /// condition makes before acting (with the cards still marked). Written as the official scripts of each timing:
    /// "your next Standby Phase" as PSY-Framelord Zeta's, "the next Standby Phase" as Bunny Ear Enthusiast's.
    /// </summary>
    public static (string Code, string[] Setup, string? Test) Timing(string when, string v) => when switch
    {
        "standby_your" =>
        ("EVENT_PHASE+PHASE_STANDBY",
        [
            "local rct=Duel.GetTurnPlayer()==tp and Duel.GetCurrentPhase()==PHASE_STANDBY and 2 or 1",
            $"{v}:SetLabel(fid,rct==2 and Duel.GetTurnCount() or 0)",
            $"{v}:SetReset(RESET_PHASE+PHASE_STANDBY+RESET_SELF_TURN,rct)",
        ], "Duel.GetTurnPlayer()==tp and Duel.GetTurnCount()~=turn"),
        // The same for the opponent's turns (Number 70: Malevolent Sin's "until your opponent's next Standby Phase").
        "standby_opp" =>
        ("EVENT_PHASE+PHASE_STANDBY",
        [
            "local rct=Duel.GetTurnPlayer()~=tp and Duel.GetCurrentPhase()==PHASE_STANDBY and 2 or 1",
            $"{v}:SetLabel(fid,rct==2 and Duel.GetTurnCount() or 0)",
            $"{v}:SetReset(RESET_PHASE+PHASE_STANDBY+RESET_OPPO_TURN,rct)",
        ], "Duel.GetTurnPlayer()~=tp and Duel.GetTurnCount()~=turn"),
        "standby_next" =>
        ("EVENT_PHASE+PHASE_STANDBY",
        [
            "local rct=Duel.GetCurrentPhase()<=PHASE_STANDBY and 2 or 1",
            $"{v}:SetLabel(fid,Duel.GetTurnCount()+1)",
            $"{v}:SetReset(RESET_PHASE+PHASE_STANDBY,rct)",
        ], "Duel.GetTurnCount()>=turn"),
        "end_next" =>
        ("EVENT_PHASE+PHASE_END",
        [
            $"{v}:SetLabel(fid,Duel.GetTurnCount()+1)",
            $"{v}:SetReset(RESET_PHASE+PHASE_END,2)",
        ], "Duel.GetTurnCount()>=turn"),
        "battle_end" =>
        ("EVENT_PHASE+PHASE_BATTLE",
        [
            $"{v}:SetLabel(fid,0)",
            $"{v}:SetReset(RESET_PHASE+PHASE_END)",
        ], null),
        _ => ("EVENT_PHASE+PHASE_END", [$"{v}:SetLabel(fid,0)"], null),
    };

    /// <summary>
    /// Lines that mark the cards of group <paramref name="g"/> with this card's field id and register the delayed effect
    /// that does <paramref name="act"/> (a call on "g") to the ones still marked, at <paramref name="when"/>.
    /// </summary>
    public static List<string> Delayed(ScriptBuilder script, string g, string when, string act, string flag = "id+9120")
    {
        var filter = script.AddFunction("laterfilter", "c,fid,f", ["return c:GetFlagEffectLabel(f)==fid"]);
        var (code, setup, test) = Timing(when, "de");
        var con = script.AddFunction("latercon", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local fid,turn=e:GetLabel()",
            .. test is null ? Array.Empty<string>() : [$"if not ({test}) then return false end"],
            $"if not e:GetLabelObject():IsExists({filter},1,nil,fid,{flag}) then",
            "\te:GetLabelObject():DeleteGroup()",
            "\te:Reset()",
            "\treturn false",
            "end",
            "return true",
        ]);
        var op = script.AddFunction("laterop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local fid=e:GetLabel()",
            $"local g=e:GetLabelObject():Filter({filter},nil,fid,{flag})",
            "e:GetLabelObject():DeleteGroup()",
            "e:Reset()",
            act,
        ]);
        return
        [
            "local fid=e:GetHandler():GetFieldID()",
            $"for tc in aux.Next({g}) do tc:RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD,0,1,fid) end",
            $"{g}:KeepAlive()",
            "local de=Effect.CreateEffect(e:GetHandler())",
            "de:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            $"de:SetCode({code})",
            "de:SetCountLimit(1)",
            "de:SetProperty(EFFECT_FLAG_IGNORE_IMMUNE)",
            .. setup,
            $"de:SetLabelObject({g})",
            $"de:SetCondition({con})",
            $"de:SetOperation({op})",
            "Duel.RegisterEffect(de,tp)",
        ];
    }

    /// <summary>
    /// "Banish it until your next Standby Phase": banished for now (REASON_TEMPORARY, in the position it is in, as official
    /// scripts banish with position 0), and returned to the field then if it is still the same banished card. The call
    /// on <paramref name="g"/>, returning how many were banished.
    /// </summary>
    public static string BanishUntil(ScriptBuilder script, string g, string when) =>
        script.AddFunction("banishuntil", "g,e,tp",
        [
            "local n=Duel.Remove(g,0,REASON_EFFECT+REASON_TEMPORARY)",
            "local og=Duel.GetOperatedGroup():Filter(Card.IsLocation,nil,LOCATION_REMOVED)",
            "if og:GetCount()==0 then return n end",
            .. Delayed(script, "og", when, "for tc in aux.Next(g) do Duel.ReturnToField(tc) end"),
            "return n",
        ]) + $"({g},e,tp)";

    /// <summary>The delayed step on "g": the Special Summon checks each card can be Summoned and the zones free, as delayed Summons do.</summary>
    private static string Act(ScriptBuilder script, string how)
    {
        if (how != "special") return how == "hand" ? "Duel.SendtoHand(g,nil,REASON_EFFECT)" : EndPhaseAction.Call(how, "g");
        return script.AddFunction("laterspsummon", "e,tp,g",
        [
            "local sg=g:Filter(Card.IsCanBeSpecialSummoned,nil,e,0,tp,false,false)",
            "local ft=Duel.GetLocationCount(tp,LOCATION_MZONE)",
            "if ft<=0 or sg:GetCount()==0 then return end",
            "if Duel.IsPlayerAffectedByEffect(tp,59822133) then ft=1 end",
            "if sg:GetCount()>ft then",
            "\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)",
            "\tsg=sg:Select(tp,ft,ft,nil)",
            "end",
            "Duel.SpecialSummon(sg,0,tp,tp,false,false,POS_FACEUP)",
        ]) + "(e,tp,g)";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var what = ctx.Args.Choice("what");
        var slot = ctx.Args.Choice(Memory.SlotKey);
        if (what == "memory") Memory.CheckRead(ctx, slot, "cards");
        if (Memory.GroupOf(ctx, what, slot) is not { } group) return;
        var how = ctx.Args.Choice("how");
        if (how == "special") e.AddCategory("CATEGORY_SPECIAL_SUMMON");
        var g = e.NewLocal("lg");
        var r = e.NewLocal("r");
        var lines = new List<string> { $"local {r}=0", $"local {g}={group}", $"if {g}:GetCount()>0 then" };
        lines.AddRange(Delayed(ctx.Script, g, ctx.Args.Choice("when"), Act(ctx.Script, how)).Select(l => "\t" + l));
        lines.Add($"\t{r}={g}:GetCount()");
        lines.Add("end");
        e.AddStep(lines, r, ctx.Link);
    }
}
