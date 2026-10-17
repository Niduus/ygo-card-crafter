using System.Globalization;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardCrafter.App.ViewModels;

/// <summary>One piece of a block's sentence: fixed text or a blank the user fills in.</summary>
public abstract class SegmentViewModel : ObservableObject;

public sealed class TextSegmentViewModel(string text) : SegmentViewModel
{
    public string Text { get; } = text;
}

public sealed class IntSegmentViewModel(ParamSpec spec, BlockInstance inst, Action changed) : SegmentViewModel
{
    public string Label => spec.Label;
    public decimal Minimum => spec.Min;
    public decimal Maximum => spec.Max;
    public decimal Increment => spec.Max > 1000 ? 100 : 1;
    public double Width => spec.Max > 999 ? 76 : 48;

    /// <summary>False while the blank doesn't apply, like an ATK amount for "cannot be destroyed".</summary>
    public bool IsShown => spec.ShownWhen?.Invoke(inst) ?? true;
    public void RefreshShown() => OnPropertyChanged(nameof(IsShown));

    public decimal? Value
    {
        get => decimal.TryParse(inst.Values.GetValueOrDefault(spec.Key, spec.Default), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        set
        {
            var text = ((int)Math.Clamp(value ?? spec.Min, spec.Min, spec.Max)).ToString(CultureInfo.InvariantCulture);
            if (inst.Values.GetValueOrDefault(spec.Key) == text) return;
            inst.Values[spec.Key] = text;
            OnPropertyChanged();
            changed();
        }
    }
}

/// <summary>A number written in hexadecimal, like a counter's engine id ("0x1f"): typed as text, kept as a number.</summary>
public sealed class HexSegmentViewModel(ParamSpec spec, BlockInstance inst, Action changed) : SegmentViewModel
{
    public string Label => spec.Label;
    public bool IsShown => spec.ShownWhen?.Invoke(inst) ?? true;
    public void RefreshShown() => OnPropertyChanged(nameof(IsShown));

    /// <summary>Parses "0x1f", "1f" or "x1F"; null when it isn't a number in range.</summary>
    public static int? Parse(string? text, int min, int max)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        else if (t.StartsWith('x') || t.StartsWith('X')) t = t[1..];
        return int.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : null;
    }

    public string Text
    {
        get => int.TryParse(inst.Values.GetValueOrDefault(spec.Key, spec.Default), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? "0x" + v.ToString("x", CultureInfo.InvariantCulture) : "";
        set
        {
            // Half-typed text stays in the box; only a whole number is kept.
            if (Parse(value, spec.Min, spec.Max) is not { } v) return;
            var text = v.ToString(CultureInfo.InvariantCulture);
            if (inst.Values.GetValueOrDefault(spec.Key) == text) return;
            inst.Values[spec.Key] = text;
            changed();
        }
    }
}

/// <param name="project">The card being edited, for choices that say how the card uses them (the Memories its blocks fill).</param>
public sealed class ChoiceSegmentViewModel(ParamSpec spec, BlockInstance inst, Action changed, Func<CardProject?>? project = null) : SegmentViewModel
{
    public string Label => spec.Label;
    public IReadOnlyList<ChoiceOption> Choices => spec.EditorChoices is { } editor && project?.Invoke() is { } p ? editor(p) : spec.Choices;

    /// <summary>False while the blank doesn't apply, like "from the GY" on a Normal Summon.</summary>
    public bool IsShown => spec.ShownWhen?.Invoke(inst) ?? true;
    public void RefreshShown() => OnPropertyChanged(nameof(IsShown));

    /// <summary>Choices that depend on the rest of the card are read again after any change to it.</summary>
    public void RefreshChoices()
    {
        if (spec.EditorChoices is null) return;
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Selected));
    }

    public ChoiceOption? Selected
    {
        get => Choices.FirstOrDefault(c => c.Value == inst.Values.GetValueOrDefault(spec.Key, spec.Default)) ?? Choices[0];
        set
        {
            if (value is null || inst.Values.GetValueOrDefault(spec.Key, spec.Default) == value.Value) return;
            inst.Values[spec.Key] = value.Value;
            OnPropertyChanged();
            changed();
        }
    }
}

public sealed class BoolSegmentViewModel(ParamSpec spec, BlockInstance inst, Action changed) : SegmentViewModel
{
    public string Label => spec.Label;

    public bool Value
    {
        get => bool.TryParse(inst.Values.GetValueOrDefault(spec.Key, spec.Default), out var b) && b;
        set
        {
            if (Value == value) return;
            inst.Values[spec.Key] = value ? "true" : "false";
            OnPropertyChanged();
            changed();
        }
    }
}

public sealed class CardsSegmentViewModel : SegmentViewModel
{
    private readonly BlockInstance _inst;
    private readonly ParamSpec _spec;
    private readonly Func<CardPack> _pack;

    public CardsSegmentViewModel(ParamSpec spec, BlockInstance inst, Func<CardPack> pack, Action changed)
    {
        _inst = inst;
        _spec = spec;
        _pack = pack;
        Label = spec.Label;
        if (!inst.Filters.TryGetValue(spec.Key, out var filter))
            inst.Filters[spec.Key] = filter = spec.DefaultFilter?.Invoke() ?? new CardFilter();
        Editor = new FilterEditorViewModel(filter, spec.AllowedZones, materialMode: false, pack, () =>
        {
            OnPropertyChanged(nameof(Summary));
            changed();
        }, allowThisCard: spec.AllowThisCard, whereFromBlock: spec.WhereFromBlock);
    }

    public string Label { get; }
    public FilterEditorViewModel Editor { get; }

    /// <summary>False while the picker doesn't apply, like "which cards" for a value that isn't a count.</summary>
    public bool IsShown => _spec.ShownWhen?.Invoke(_inst) ?? true;
    public void RefreshShown() => OnPropertyChanged(nameof(IsShown));

    /// <summary>"cards your opponent controls" when the block takes more than one card, as the card text says it.</summary>
    public string Summary
    {
        get
        {
            var filter = _inst.Filters[_spec.Key];
            if (filter.ThisCard) return "this card";
            var count = int.TryParse(_inst.Values.GetValueOrDefault("count"), out var n) ? n : 1;
            var plural = count != 1 || _inst.Values.GetValueOrDefault("mode") == "all";
            return Core.Text.FilterText.Describe(filter, _pack(), plural);
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(Summary));
}
