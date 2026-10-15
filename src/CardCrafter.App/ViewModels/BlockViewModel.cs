using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>One placed block on the event sheet, shown as a sentence with editable blanks.</summary>
public sealed partial class BlockViewModel : ObservableObject
{
    private readonly Action _changed;

    public BlockViewModel(BlockDefinition definition, BlockInstance instance, EffectViewModel owner, Func<CardPack> pack, Action changed)
    {
        Definition = definition;
        Instance = instance;
        Owner = owner;
        _changed = changed;
        foreach (var seg in SentenceParser.Parse(definition.Sentence))
        {
            if (seg.Key is null)
            {
                // Punctuation like a lone "," reads fine in card text but looks stray between controls.
                var text = seg.Text.Trim().Trim(',').Trim();
                if (text.Length > 0) Segments.Add(new TextSegmentViewModel(text));
                continue;
            }
            var spec = definition.Param(seg.Key);
            if (spec is null) continue;
            Segments.Add(spec.Type switch
            {
                ParamType.Integer => new IntSegmentViewModel(spec, instance, changed),
                ParamType.Choice => new ChoiceSegmentViewModel(spec, instance, changed),
                ParamType.Bool => new BoolSegmentViewModel(spec, instance, changed),
                _ => new CardsSegmentViewModel(spec, instance, pack, changed),
            });
        }
        // Flags that are not in the sentence (like "optional") go at the end.
        foreach (var spec in definition.Params.Where(p => p.Type == ParamType.Bool && !definition.Sentence.Contains("{" + p.Key + "}")))
            Segments.Add(new BoolSegmentViewModel(spec, instance, changed));
    }

    public BlockDefinition Definition { get; }
    public BlockInstance Instance { get; }
    public EffectViewModel Owner { get; }
    public List<SegmentViewModel> Segments { get; } = [];

    public string Kind => Definition.Kind.ToString();
    public string KindLabel => Definition.Kind switch
    {
        BlockKind.Trigger => "WHEN",
        BlockKind.Condition => "IF",
        BlockKind.Cost => "COST",
        BlockKind.Passive => "ALWAYS",
        _ => "DO",
    };
    public string Help => Definition.Help.Length > 0 ? Definition.Help : Definition.Name;
    public bool IsTrigger => Definition.Kind == BlockKind.Trigger;

    /// <summary>Only DO blocks after the first show how they link to the one before.</summary>
    [ObservableProperty] private bool _showLink;

    public IReadOnlyList<Option<ActionLink>> LinkOptions => Lists.Links;
    public Option<ActionLink> Link
    {
        get => Options.Find(Lists.Links, Instance.Link);
        set
        {
            if (value is null || value.Value == Instance.Link) return;
            Instance.Link = value.Value;
            OnPropertyChanged();
            _changed();
        }
    }

    [RelayCommand] private void Remove() => Owner.RemoveBlock(this);
    [RelayCommand] private void MoveUp() => Owner.MoveBlock(this, -1);
    [RelayCommand] private void MoveDown() => Owner.MoveBlock(this, +1);
}
