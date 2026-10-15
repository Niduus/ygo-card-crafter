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
        // Changing how many cards changes the cards' wording ("card" or "cards").
        void Changed()
        {
            foreach (var c in Segments.OfType<CardsSegmentViewModel>()) c.Refresh();
            changed();
        }
        var mode = definition.Param("mode");
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
            // "exactly / up to / all" goes right before the count it qualifies.
            if (spec.Key == "count" && mode is not null && !definition.Sentence.Contains("{mode}"))
                Segments.Add(new ChoiceSegmentViewModel(mode, instance, Changed));
            Segments.Add(spec.Type switch
            {
                ParamType.Integer => new IntSegmentViewModel(spec, instance, Changed),
                ParamType.Choice => new ChoiceSegmentViewModel(spec, instance, Changed),
                ParamType.Bool => new BoolSegmentViewModel(spec, instance, Changed),
                _ => new CardsSegmentViewModel(spec, instance, pack, Changed),
            });
        }
        // Choices and flags that are not in the sentence (like "for each" or "optional") go at the end.
        foreach (var spec in definition.Params.Where(p => p.Type is ParamType.Choice or ParamType.Bool && p.Key != "mode" && !definition.Sentence.Contains("{" + p.Key + "}")))
            Segments.Add(spec.Type == ParamType.Bool ? new BoolSegmentViewModel(spec, instance, Changed) : new ChoiceSegmentViewModel(spec, instance, Changed));
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
    /// <summary>An "or when" trigger after the first one.</summary>
    [ObservableProperty] private bool _isAlsoWhen;

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

    /// <summary>"You can ...": only DO blocks of activated effects can be skipped.</summary>
    public bool ShowOptional => Definition.Kind == BlockKind.Action && !Owner.IsPassive;

    public bool Optional
    {
        get => Instance.Optional;
        set
        {
            if (Instance.Optional == value) return;
            Instance.Optional = value;
            OnPropertyChanged();
            _changed();
        }
    }

    [RelayCommand] private void Remove() => Owner.RemoveBlock(this);
    [RelayCommand] private void MoveUp() => Owner.MoveBlock(this, -1);
    [RelayCommand] private void MoveDown() => Owner.MoveBlock(this, +1);
}
