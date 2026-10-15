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

public sealed class ChoiceSegmentViewModel(ParamSpec spec, BlockInstance inst, Action changed) : SegmentViewModel
{
    public string Label => spec.Label;
    public IReadOnlyList<ChoiceOption> Choices => spec.Choices;

    public ChoiceOption? Selected
    {
        get => spec.Choices.FirstOrDefault(c => c.Value == inst.Values.GetValueOrDefault(spec.Key, spec.Default)) ?? spec.Choices[0];
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
    public CardsSegmentViewModel(ParamSpec spec, BlockInstance inst, Func<CardPack> pack, Action changed)
    {
        Label = spec.Label;
        if (!inst.Filters.TryGetValue(spec.Key, out var filter))
            inst.Filters[spec.Key] = filter = spec.DefaultFilter?.Invoke() ?? new CardFilter();
        Editor = new FilterEditorViewModel(filter, spec.AllowedZones, materialMode: false, pack, () =>
        {
            OnPropertyChanged(nameof(Summary));
            changed();
        });
    }

    public string Label { get; }
    public FilterEditorViewModel Editor { get; }
    public string Summary => Editor.Summary;
}
