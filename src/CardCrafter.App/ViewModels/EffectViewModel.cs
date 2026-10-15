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
    public bool IsPassive => Model.When?.BlockId == "when.while_face_up";
    /// <summary>A Special Summon procedure: the Summon is what it does, so it has no DO blocks.</summary>
    public bool IsProcedure => Model.When?.BlockId == SummonProcedureTrigger.BlockId;
    public bool ShowCosts => !IsPassive;
    public bool ShowLimit => !IsPassive;
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
            _changed();
        }
    }

    public bool ShowSharedGroup => Model.Limit == OncePerTurn.Hard;
    public decimal? SharedGroup
    {
        get => Model.SharedLimitGroup;
        set { if (Model.SharedLimitGroup == (int)(value ?? 0)) return; Model.SharedLimitGroup = (int)(value ?? 0); OnPropertyChanged(); _changed(); }
    }

    /// <summary>Blocks offered by each "+" menu. Lasting effects only offer lasting DO blocks.</summary>
    public IEnumerable<BlockDefinition> Available(BlockKind kind) => kind switch
    {
        BlockKind.Action => BlockRegistry.OfKind(IsPassive ? BlockKind.Passive : BlockKind.Action),
        _ => BlockRegistry.OfKind(kind),
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
        Rebuild();
        _changed();
    }

    public void AddBlock(BlockDefinition def)
    {
        var inst = def.CreateInstance();
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
        if (block.IsTrigger) Model.When = null;
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

    private List<BlockInstance>? ListFor(BlockViewModel block) => block.Definition.Kind switch
    {
        BlockKind.Condition => Model.If,
        BlockKind.Cost => Model.Cost,
        BlockKind.Action or BlockKind.Passive => Model.Do,
        _ => null,
    };

    private void Rebuild()
    {
        Fill(Trigger, Model.When is null ? [] : [Model.When]);
        Fill(Conditions, Model.If);
        Fill(Costs, Model.Cost);
        Fill(Actions, Model.Do);
        for (int i = 0; i < Actions.Count; i++) Actions[i].ShowLink = i > 0 && !IsPassive;
        OnPropertyChanged(nameof(HasTrigger));
        OnPropertyChanged(nameof(IsPassive));
        OnPropertyChanged(nameof(IsProcedure));
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

    [RelayCommand] private void Remove() => Owner.RemoveEffect(this);
    [RelayCommand] private void MoveUp() => Owner.MoveEffect(this, -1);
    [RelayCommand] private void MoveDown() => Owner.MoveEffect(this, +1);
    [RelayCommand] private void Duplicate() => Owner.DuplicateEffect(this);
}
