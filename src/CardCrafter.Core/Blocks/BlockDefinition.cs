using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>Which column of the event sheet a block belongs in.</summary>
public enum BlockKind
{
    /// <summary>WHEN: what starts the effect. Exactly one per effect.</summary>
    Trigger,
    /// <summary>IF: something that must be true for the effect to be used.</summary>
    Condition,
    /// <summary>COST: paid when the effect is activated.</summary>
    Cost,
    /// <summary>DO: what happens when the effect resolves.</summary>
    Action,
    /// <summary>DO, for "while face-up" effects: a lasting change instead of a one-off action.</summary>
    Passive,
}

public enum ParamType { Integer, Choice, Bool, Cards }

/// <param name="Label">Wording used in card text. May be empty, e.g. "from anywhere" reads as nothing.</param>
/// <param name="Ui">Wording shown in the editor when it should differ from <paramref name="Label"/>.</param>
public sealed record ChoiceOption(string Value, string Label, string? Ui = null)
{
    public string Display => Ui ?? (Label.Trim().Length > 0 ? Label.Trim() : Value);
}

/// <summary>One fill-in-the-blank slot in a block's sentence, written as {key} in the sentence.</summary>
public sealed class ParamSpec
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required ParamType Type { get; init; }
    public string Default { get; init; } = "";
    public int Min { get; init; } = 0;
    public int Max { get; init; } = 999999;
    public IReadOnlyList<ChoiceOption> Choices { get; init; } = [];
    public Func<CardFilter>? DefaultFilter { get; init; }
    /// <summary>For card filters: zones the user may pick from. None means any.</summary>
    public Zone AllowedZones { get; init; }
    /// <summary>For card filters: the picker offers "This card itself".</summary>
    public bool AllowThisCard { get; init; }
    /// <summary>For card filters: the block says where the card is, so the picker only asks what the card is.</summary>
    public bool WhereFromBlock { get; init; }
    /// <summary>For numbers: the editor shows and takes it in hexadecimal ("0x1f"), as engine ids are written.</summary>
    public bool Hex { get; init; }

    /// <summary>When set, the editor shows this blank only while it applies, like "from the GY" for Special Summons only.</summary>
    public Func<BlockInstance, bool>? ShownWhen { get; private set; }

    public ParamSpec ShowIf(Func<BlockInstance, bool> shown)
    {
        ShownWhen = shown;
        return this;
    }

    public static ParamSpec Int(string key, string label, int def, int min = 0, int max = 999999) =>
        new() { Key = key, Label = label, Type = ParamType.Integer, Default = def.ToString(CultureInfo.InvariantCulture), Min = min, Max = max };

    /// <summary>A number the editor shows in hexadecimal, like a counter's engine id.</summary>
    public static ParamSpec HexInt(string key, string label, int def, int min, int max) =>
        new() { Key = key, Label = label, Type = ParamType.Integer, Default = def.ToString(CultureInfo.InvariantCulture), Min = min, Max = max, Hex = true };

    public static ParamSpec Choice(string key, string label, params ChoiceOption[] options) =>
        new() { Key = key, Label = label, Type = ParamType.Choice, Default = options[0].Value, Choices = options };

    public static ParamSpec Flag(string key, string label, bool def) =>
        new() { Key = key, Label = label, Type = ParamType.Bool, Default = def ? "true" : "false" };

    /// <param name="thisCard">The picker also offers "This card itself".</param>
    public static ParamSpec Cards(string key, string label, Func<CardFilter> def, Zone allowed = Zone.None, bool thisCard = false) =>
        new() { Key = key, Label = label, Type = ParamType.Cards, DefaultFilter = def, AllowedZones = allowed, AllowThisCard = thisCard };
}

/// <summary>
/// A block the user can place on the event sheet. Each block knows three things:
/// how it reads in plain English, what blanks the user fills in, and what Lua it produces.
/// </summary>
public abstract class BlockDefinition
{
    public abstract string Id { get; }
    public abstract BlockKind Kind { get; }
    /// <summary>Short name for menus, e.g. "Draw cards".</summary>
    public abstract string Name { get; }
    /// <summary>Menu group, e.g. "Cards" or "Life Points".</summary>
    public virtual string Group => "General";
    /// <summary>The sentence shown on the sheet, with {key} blanks, e.g. "Draw {count} card(s)".</summary>
    public abstract string Sentence { get; }
    public virtual string Help => "";
    public virtual IReadOnlyList<ParamSpec> Params => [];

    public abstract void Compile(BlockContext ctx);

    /// <summary>Card-text wording for this block with its values filled in.</summary>
    public virtual string Describe(BlockArgs args) => FillSentence(Sentence, args);

    /// <summary>
    /// For WHEN blocks: how the trigger reads when several of them start one effect, so "Normal Summoned" and
    /// "Special Summoned" read as "If this card is Normal or Special Summoned". Null when it doesn't combine.
    /// </summary>
    public virtual EventPhrase? Phrase(BlockArgs args) => null;

    /// <summary>
    /// Extra menu entries that add this block already set up, so "Discard this card" is found by name rather than
    /// inside "Tribute, banish, send or discard this card".
    /// </summary>
    public virtual IReadOnlyList<MenuShortcut> Shortcuts => [];

    /// <summary>Kept so older packs load, but no longer offered in the menus (another block does its job).</summary>
    public virtual bool Hidden => false;

    /// <summary>A sentence that goes after the effect's text, e.g. a restriction a cost puts on the turn. Usually empty.</summary>
    public virtual string Afterword(BlockArgs args) => "";

    /// <summary>
    /// For targeting actions: the "target ..." clause that goes before the semicolon, and the action
    /// wording that follows it, e.g. ("target 1 monster your opponent controls", "destroy it").
    /// Null when the block is not currently targeting.
    /// </summary>
    public virtual (string Target, string Action)? DescribeTargeting(BlockArgs args) => null;

    public ParamSpec? Param(string key) => Params.FirstOrDefault(p => p.Key == key);

    public BlockInstance CreateInstance()
    {
        var inst = new BlockInstance(Id);
        foreach (var p in Params)
        {
            if (p.Type == ParamType.Cards)
                inst.Filters[p.Key] = p.DefaultFilter?.Invoke() ?? new CardFilter();
            else
                inst.Values[p.Key] = p.Default;
        }
        return inst;
    }

    protected static string FillSentence(string sentence, BlockArgs args)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var seg in SentenceParser.Parse(sentence))
            sb.Append(seg.Key is null ? seg.Text : args.Describe(seg.Key));
        // Cards print "..., except "Card Name"" at the end of the sentence.
        return sb.ToString().TrimEnd() + args.ExceptText();
    }
}

/// <summary>A menu entry that adds a block with some of its blanks filled in, and maybe its card pickers.</summary>
public sealed record MenuShortcut(string Label, IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, Func<CardFilter>>? Filters = null)
{
    public static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();
}

/// <summary>
/// One trigger's wording for combining: <paramref name="Template"/> holds "{0}" where the triggers that share it put
/// their <paramref name="Label"/>, joined with "or": "this card is {0} Summoned" + "Normal", "Special".
/// </summary>
/// <param name="Lead">"If" or "When".</param>
/// <param name="Alone">The wording when no other trigger shares the template ("a monster", not "a monster(s)").</param>
public sealed record EventPhrase(string Lead, string Template, string Label = "", string? Alone = null);

public readonly record struct SentenceSegment(string Text, string? Key);

public static class SentenceParser
{
    /// <summary>Splits "Draw {count} card(s)" into text and blank segments.</summary>
    public static IReadOnlyList<SentenceSegment> Parse(string sentence)
    {
        var result = new List<SentenceSegment>();
        int i = 0;
        while (i < sentence.Length)
        {
            int open = sentence.IndexOf('{', i);
            if (open < 0) { result.Add(new(sentence[i..], null)); break; }
            if (open > i) result.Add(new(sentence[i..open], null));
            int close = sentence.IndexOf('}', open);
            if (close < 0) { result.Add(new(sentence[open..], null)); break; }
            result.Add(new("", sentence[(open + 1)..close]));
            i = close + 1;
        }
        return result;
    }
}

/// <summary>Typed access to the values the user entered for one placed block.</summary>
public sealed class BlockArgs(BlockDefinition def, BlockInstance inst, CardPack? pack = null)
{
    public BlockDefinition Definition { get; } = def;
    public BlockInstance Instance { get; } = inst;
    public CardPack? Pack { get; } = pack;
    /// <summary>The effect the block is in, when the wording depends on it (e.g. "from your hand" after a hand trigger).</summary>
    public EffectEvent? Effect { get; init; }
    /// <summary>The card's name, for "except "Card Name"".</summary>
    public string? CardName { get; init; }

    /// <summary>Whether the block's cards read as plural: a count other than 1, or "all".</summary>
    public bool IsPlural => (Definition.Param("count") is { Type: ParamType.Integer } && Int("count") != 1) || CountMode == "all";

    /// <summary>
    /// "exact", "up_to" or "all": how many cards a card action takes. It is "mode", or "count_mode" on blocks whose
    /// "mode" says something else ("Level becomes / increases by").
    /// </summary>
    public string CountMode => Definition.Param("count_mode") is not null ? Choice("count_mode")
        : Definition.Param("mode") is { } m && m.Choices.Any(c => c.Value is "all" or "up_to") ? Choice("mode") : "exact";

    /// <summary>", except "Card Name"" for this block's card filters, or "".</summary>
    public string ExceptText() => string.Concat(Definition.Params.Where(p => p.Type == ParamType.Cards).Select(p => Text.FilterText.ExceptText(Cards(p.Key), Pack, CardName)));

    /// <summary>True when the block's cards are "this card itself".</summary>
    public bool IsThisCard => Definition.Params.Any(p => p.Type == ParamType.Cards && Cards(p.Key).ThisCard);

    /// <summary>Where this card is while its effect is used, from the WHEN block's zone, or null.</summary>
    public string? TriggerZone => Effect?.When is { } w && w.Values.TryGetValue("zone", out var z) ? z : null;

    public int Int(string key)
    {
        var spec = Definition.Param(key) ?? throw new ArgumentException($"Block {Definition.Id} has no parameter {key}");
        var raw = Instance.Values.GetValueOrDefault(key, spec.Default);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            int.TryParse(spec.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        return Math.Clamp(v, spec.Min, spec.Max);
    }

    public string Choice(string key)
    {
        var spec = Definition.Param(key) ?? throw new ArgumentException($"Block {Definition.Id} has no parameter {key}");
        var raw = Instance.Values.GetValueOrDefault(key, spec.Default);
        return spec.Choices.Any(c => c.Value == raw) ? raw : spec.Default;
    }

    public bool Bool(string key)
    {
        var spec = Definition.Param(key) ?? throw new ArgumentException($"Block {Definition.Id} has no parameter {key}");
        var raw = Instance.Values.GetValueOrDefault(key, spec.Default);
        return bool.TryParse(raw, out var b) ? b : spec.Default == "true";
    }

    public CardFilter Cards(string key)
    {
        var spec = Definition.Param(key) ?? throw new ArgumentException($"Block {Definition.Id} has no parameter {key}");
        if (Instance.Filters.TryGetValue(key, out var f)) return f;
        return spec.DefaultFilter?.Invoke() ?? new CardFilter();
    }

    /// <summary>Plain-English text for one blank.</summary>
    public string Describe(string key)
    {
        var spec = Definition.Param(key);
        if (spec is null) return "{" + key + "}";
        return spec.Type switch
        {
            // "Special Summon this card", not "Special Summon 1 this card".
            ParamType.Integer when key == "count" && IsThisCard => "",
            // "up to 3", "all": card actions say how many with their count.
            ParamType.Integer when key == "count" && CountMode == "all" => "all",
            ParamType.Integer when key == "count" && CountMode == "up_to" => $"up to {Int(key).ToString(CultureInfo.InvariantCulture)}",
            ParamType.Integer => Int(key).ToString(CultureInfo.InvariantCulture),
            ParamType.Choice => spec.Choices.First(c => c.Value == Choice(key)).Label,
            ParamType.Bool => Bool(key) ? spec.Label : "",
            // "Discard 2 cards": a block's "count" blank decides whether its cards are plural.
            ParamType.Cards when Cards(key).ThisCard => TriggerZone is { } z && Zones.From(z) is { } from ? $"this card {from}" : "this card",
            ParamType.Cards => Text.FilterText.Describe(Cards(key), Pack, plural: IsPlural, ownName: CardName, withExcept: false),
            _ => "",
        };
    }
}
