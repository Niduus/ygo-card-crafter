using System.Collections.ObjectModel;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>One effect: a row of the event sheet with WHEN/IF on the left and COST/DO on the right.</summary>
public sealed partial class EffectViewModel : ObservableObject
{
    private readonly Func<CardPack> _pack;
    private readonly Action _changed;

    public EffectViewModel(EffectEvent model, CardViewModel owner, Func<CardPack> pack, Action changed)
    {
        Model = model;
        // Older combined WHEN choices show as one WHEN per event with "Or when".
        TriggerSplit.Normalize(model);
        Owner = owner;
        _pack = pack;
        _changed = changed;
        Rebuild();
    }

    public EffectEvent Model { get; }
    public CardViewModel Owner { get; }

    [ObservableProperty] private int _number;
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _problems = "";
    public bool HasProblems => Problems.Length > 0;
    partial void OnProblemsChanged(string value) => OnPropertyChanged(nameof(HasProblems));

    public ObservableCollection<BlockViewModel> Trigger { get; } = [];
    public ObservableCollection<BlockViewModel> Conditions { get; } = [];
    public ObservableCollection<BlockViewModel> Costs { get; } = [];
    public ObservableCollection<BlockViewModel> Actions { get; } = [];

    public bool HasTrigger => Trigger.Count > 0;
    /// <summary>"Or when ...": another trigger can start the same effect (not lasting effects or Summon procedures).</summary>
    public bool CanAddAlsoWhen => Model.When is not null && !IsPassive && !IsProcedure;

    /// <summary>Triggers offered by "Or when ...": the ones that start an activated effect.</summary>
    public IEnumerable<BlockDefinition> AlsoWhenChoices => BlockRegistry.Offered(BlockKind.Trigger)
        .Where(d => d.Id is not ("when.while_face_up" or SummonProcedureTrigger.BlockId));

    public void AddAlsoWhen(BlockDefinition def)
    {
        if (!CanAddAlsoWhen) return;
        Model.AlsoWhen.Add(NextWhen(def, Model.When!, Model.AlsoWhen));
        Rebuild();
        _changed();
    }
    /// <summary>
    /// A new "or when" block that starts like the effect's first WHEN (optional or not, where this card is, which
    /// cards) on the next event not used yet: after "Normal Summoned" comes "Special Summoned".
    /// </summary>
    public static BlockInstance NextWhen(BlockDefinition def, BlockInstance first, IReadOnlyList<BlockInstance> others)
    {
        var inst = def.CreateInstance();
        foreach (var key in new[] { "optional", "zone" })
            if (def.Param(key) is not null && first.Values.TryGetValue(key, out var value)) inst.Values[key] = value;
        if (def.Id != first.BlockId) return inst;
        foreach (var (key, value) in first.Values) inst.Values[key] = value;
        foreach (var (key, filter) in first.Filters) inst.Filters[key] = filter.Clone();
        // The block's main choice moves on to one the effect doesn't use yet.
        if (def.Params.FirstOrDefault(p => p.Type == ParamType.Choice && p.Key is not ("optional" or "zone")) is { } main)
        {
            var used = new[] { first }.Concat(others).Where(b => b.BlockId == def.Id).Select(b => b.Values.GetValueOrDefault(main.Key)).ToHashSet();
            if (main.Choices.FirstOrDefault(c => !used.Contains(c.Value)) is { } next) inst.Values[main.Key] = next.Value;
        }
        return inst;
    }

    public bool IsPassive => Model.When?.BlockId == "when.while_face_up";
    /// <summary>A Special Summon procedure: the Summon is what it does, so it has no DO blocks.</summary>
    public bool IsProcedure => Model.When?.BlockId == SummonProcedureTrigger.BlockId;
    public bool ShowCosts => !IsPassive;
    public string CostHint => IsProcedure ? "Paid to Summon it" : "Paid to activate it";
    /// <summary>Lasting effects have no limit, except replacements like "Tribute this card instead".</summary>
    public bool ShowLimit => !IsPassive || Model.Do.Any(b => b.BlockId == ReplaceDestroyPassive.BlockId);
    public string DoHeader => IsPassive ? "ALWAYS (lasting effects)" : "DO";

    public string Label
    {
        get => Model.Label;
        set { if (Model.Label == value) return; Model.Label = value; OnPropertyChanged(); _changed(); }
    }

    public IReadOnlyList<Option<OncePerTurn>> LimitOptions => Lists.Limits;
    public Option<OncePerTurn> Limit
    {
        get => Options.Find(Lists.Limits, Model.Limit);
        set
        {
            if (value is null || value.Value == Model.Limit) return;
            Model.Limit = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSharedGroup));
            OnPropertyChanged(nameof(ShowTimes));
            _changed();
        }
    }

    /// <summary>"Once per turn" effects (each copy) can share their use too, like an Ignition Effect and its Quick Effect copy.</summary>
    public bool ShowSharedGroup => Model.Limit is OncePerTurn.Hard or OncePerTurn.Duel or OncePerTurn.Soft or OncePerTurn.FaceUpAndHard or OncePerTurn.SoftAndHard;

    /// <summary>"Up to twice per turn", "thrice per turn by name": limits that can allow more than one use.</summary>
    public bool ShowTimes => Model.Limit is OncePerTurn.Hard or OncePerTurn.Duel or OncePerTurn.Soft or OncePerTurn.SoftAndHard;
    public decimal? Times
    {
        get => Model.Times;
        set
        {
            var n = Math.Clamp((int)(value ?? 1), 1, 9);
            if (Model.Times == n) return;
            Model.Times = n;
            OnPropertyChanged();
            _changed();
        }
    }
    public decimal? SharedGroup
    {
        get => Model.SharedLimitGroup;
        set { if (Model.SharedLimitGroup == (int)(value ?? 0)) return; Model.SharedLimitGroup = (int)(value ?? 0); OnPropertyChanged(); _changed(); }
    }

    /// <summary>Blocks offered by each "+" menu. Lasting effects only offer lasting DO blocks.</summary>
    public IEnumerable<BlockDefinition> Available(BlockKind kind) => kind switch
    {
        BlockKind.Action => BlockRegistry.Offered(IsPassive ? BlockKind.Passive : BlockKind.Action),
        _ => BlockRegistry.Offered(kind),
    };

    public void SetTrigger(BlockDefinition def)
    {
        var wasPassive = IsPassive;
        Model.When = def.CreateInstance();
        // Switching between activated and lasting effects invalidates the DO blocks.
        if (wasPassive != IsPassive)
        {
            Model.Do.Clear();
            Model.Cost.Clear();
        }
        // A Summon procedure keeps its IF and COST blocks but does nothing else.
        if (IsProcedure) Model.Do.Clear();
        if (IsPassive || IsProcedure) Model.AlsoWhen.Clear();
        Rebuild();
        _changed();
    }

    /// <summary>
    /// IF blocks offered among the DO blocks: the DO blocks after them happen only if they hold
    /// ("..., then if you control a Synchro Monster, destroy that card").
    /// </summary>
    public IEnumerable<BlockDefinition> AvailableGates => IsPassive ? [] : BlockRegistry.Offered(BlockKind.Condition);

    /// <summary>
    /// COST blocks offered among the DO blocks once a "Choose 1 of these effects" block leads them: an option's own cost
    /// ("● Discard 1 card; draw 1 card").
    /// </summary>
    public IEnumerable<BlockDefinition> AvailableOptionCosts =>
        IsPassive || ChooseEffectsAction.Header(Model.Do) is null ? [] : BlockRegistry.Offered(BlockKind.Cost);

    public void AddGate(BlockDefinition def)
    {
        Model.Do.Add(def.CreateInstance());
        Rebuild();
        _changed();
    }

    public void AddBlock(BlockDefinition def, IReadOnlyDictionary<string, string>? values = null, IReadOnlyDictionary<string, Func<CardFilter>>? filters = null)
    {
        var inst = def.CreateInstance();
        foreach (var (key, value) in values ?? MenuShortcut.NoValues) inst.Values[key] = value;
        foreach (var (key, make) in filters ?? new Dictionary<string, Func<CardFilter>>()) inst.Filters[key] = make();
        switch (def.Kind)
        {
            case BlockKind.Condition: Model.If.Add(inst); break;
            case BlockKind.Cost: Model.Cost.Add(inst); break;
            case BlockKind.Trigger: SetTrigger(def); return;
            default: Model.Do.Add(inst); break;
        }
        Rebuild();
        _changed();
    }

    public void RemoveBlock(BlockViewModel block)
    {
        if (Model.AlsoWhen.Contains(block.Instance)) Model.AlsoWhen.Remove(block.Instance);
        else if (block.IsTrigger)
        {
            // The next "or when" trigger becomes the first.
            Model.When = Model.AlsoWhen.FirstOrDefault();
            if (Model.AlsoWhen.Count > 0) Model.AlsoWhen.RemoveAt(0);
        }
        else ListFor(block)?.Remove(block.Instance);
        Rebuild();
        _changed();
    }

    public void MoveBlock(BlockViewModel block, int delta)
    {
        var list = ListFor(block);
        if (list is null) return;
        var i = list.IndexOf(block.Instance);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Rebuild();
        _changed();
    }

    private List<BlockInstance>? ListFor(BlockViewModel block) => Model.Do.Contains(block.Instance) ? Model.Do : block.Definition.Kind switch
    {
        BlockKind.Condition => Model.If,
        BlockKind.Cost => Model.Cost,
        BlockKind.Action or BlockKind.Passive => Model.Do,
        _ => null,
    };

    private void Rebuild()
    {
        Fill(Trigger, Model.When is null ? [] : [Model.When, .. Model.AlsoWhen]);
        for (int i = 0; i < Trigger.Count; i++) Trigger[i].IsAlsoWhen = i > 0;
        Fill(Conditions, Model.If);
        for (int i = 0; i < Conditions.Count; i++) Conditions[i].ShowOr = i > 0;
        Fill(Costs, Model.Cost);
        Fill(Actions, Model.Do);
        for (int i = 0; i < Actions.Count; i++) Actions[i].ShowLink = i > 0 && !IsPassive;
        OnPropertyChanged(nameof(HasTrigger));
        OnPropertyChanged(nameof(CanAddAlsoWhen));
        OnPropertyChanged(nameof(IsPassive));
        OnPropertyChanged(nameof(IsProcedure));
        OnPropertyChanged(nameof(CostHint));
        OnPropertyChanged(nameof(ShowCosts));
        OnPropertyChanged(nameof(ShowLimit));
        OnPropertyChanged(nameof(DoHeader));
    }

    private void Fill(ObservableCollection<BlockViewModel> target, IEnumerable<BlockInstance> source)
    {
        target.Clear();
        foreach (var inst in source)
        {
            var def = BlockRegistry.Find(inst.BlockId);
            if (def is not null) target.Add(new BlockViewModel(def, inst, this, _pack, _changed));
        }
    }

    /// <summary>Re-reads values after the pack's archetypes change.</summary>
    public void RefreshFilters()
    {
        foreach (var b in Trigger.Concat(Conditions).Concat(Costs).Concat(Actions))
            foreach (var s in b.Segments.OfType<CardsSegmentViewModel>())
                s.Editor.RefreshArchetypes();
    }

    /// <summary>Re-reads the choices that depend on the rest of the card (the Memories its blocks fill).</summary>
    public void RefreshChoices()
    {
        foreach (var b in Trigger.Concat(Conditions).Concat(Costs).Concat(Actions)) b.RefreshChoices();
    }

    [RelayCommand] private void Remove() => Owner.RemoveEffect(this);
    [RelayCommand] private void MoveUp() => Owner.MoveEffect(this, -1);
    [RelayCommand] private void MoveDown() => Owner.MoveEffect(this, +1);
    [RelayCommand] private void Duplicate() => Owner.DuplicateEffect(this);
}
