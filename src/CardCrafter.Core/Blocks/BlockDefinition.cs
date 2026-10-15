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

    public static ParamSpec Int(string key, string label, int def, int min = 0, int max = 999999) =>
        new() { Key = key, Label = label, Type = ParamType.Integer, Default = def.ToString(CultureInfo.InvariantCulture), Min = min, Max = max };

    public static ParamSpec Choice(string key, string label, params ChoiceOption[] options) =>
        new() { Key = key, Label = label, Type = ParamType.Choice, Default = options[0].Value, Choices = options };

    public static ParamSpec Flag(string key, string label, bool def) =>
        new() { Key = key, Label = label, Type = ParamType.Bool, Default = def ? "true" : "false" };

    public static ParamSpec Cards(string key, string label, Func<CardFilter> def, Zone allowed = Zone.None) =>
        new() { Key = key, Label = label, Type = ParamType.Cards, DefaultFilter = def, AllowedZones = allowed };
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
        return sb.ToString();
    }
}

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
            ParamType.Integer => Int(key).ToString(CultureInfo.InvariantCulture),
            ParamType.Choice => spec.Choices.First(c => c.Value == Choice(key)).Label,
            ParamType.Bool => Bool(key) ? spec.Label : "",
            // "Discard 2 cards": a block's "count" blank decides whether its cards are plural.
            ParamType.Cards => Text.FilterText.Describe(Cards(key), Pack, plural: Definition.Param("count") is { Type: ParamType.Integer } && Int("count") != 1),
            _ => "",
        };
    }
}
