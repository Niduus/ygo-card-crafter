using System.Collections.ObjectModel;
using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Templates;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

public sealed partial class ArchetypeChoice(Archetype archetype, bool selected, Action<ArchetypeChoice> toggled) : ObservableObject
{
    public Archetype Archetype { get; } = archetype;
    public string Label => $"{Archetype.Name} (0x{Archetype.Code:x})";

    private bool _isSelected = selected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) toggled(this); }
    }
}

public sealed record DiagnosticItem(Severity Severity, string Text)
{
    public bool IsError => Severity == Severity.Error;
    public string Icon => Severity switch { Severity.Error => "✖", Severity.Warning => "⚠", _ => "ℹ" };
}

/// <summary>Everything about one card in the editor: its data, its event sheet, and the generated script.</summary>
public sealed partial class CardViewModel : ObservableObject
{
    private readonly Func<CardPack> _pack;
    private readonly Action _dirty;
    private bool _refreshing;

    private readonly Func<OmegaCatalog> _catalog;
    private readonly Func<long, long> _newPasscode;

    public CardViewModel(CardProject project, Func<CardPack> pack, Action dirty, Func<OmegaCatalog>? catalog = null, Func<long, long>? newPasscode = null)
    {
        Project = project;
        _pack = pack;
        _dirty = dirty;
        _catalog = catalog ?? (() => OmegaCatalog.Empty);
        _newPasscode = newPasscode ?? (except => PasscodeGenerator.Generate(pack().Cards.Select(c => c.Card.Id).Where(id => id != except)));
        foreach (var e in project.Effects) Effects.Add(new EffectViewModel(e, this, pack, Changed));
        MaterialFilter = new FilterEditorViewModel(project.Procedure.MaterialFilter, Zone.None, true, pack, Changed);
        TunerFilter = new FilterEditorViewModel(project.Procedure.TunerFilter, Zone.None, true, pack, Changed);
        RebuildArchetypes();
        Refresh();
    }

    public CardProject Project { get; }
    private CardData C => Project.Card;
    public ObservableCollection<EffectViewModel> Effects { get; } = [];

    // ---- outputs, recomputed on every change
    [ObservableProperty] private string _lua = "";
    [ObservableProperty] private string _suggestedText = "";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = [];
    public bool HasErrors => ErrorCount > 0;
    public bool NoProblems => Diagnostics.Count == 0;
    public string StatusIcon => ErrorCount > 0 ? "✖" : WarningCount > 0 ? "⚠" : "✔";
    public string ListLabel => string.IsNullOrWhiteSpace(C.Name) ? "(unnamed)" : C.Name;
    public string ListDetail => $"{C.Id}  ·  {KindText}";
    private string KindText => C.Kind switch
    {
        CardKind.Monster => $"{C.Frame} Monster",
        CardKind.Spell => $"{Options.Find(Lists.SpellKinds, C.SpellKind).Label} Spell",
        _ => $"{Options.Find(Lists.TrapKinds, C.TrapKind).Label} Trap",
    };

    /// <summary>Called by every editor control when the user changes something.</summary>
    public void Changed()
    {
        if (_refreshing) return;
        _dirty();
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            var pack = _pack();
            var result = ScriptCompiler.Compile(Project, pack);
            Lua = result.Lua;
            var all = result.Diagnostics.ToList();
            if (!result.HasErrors) all.AddRange(ScriptChecker.Check(result.Lua));
            var passcode = PasscodeProblems(pack);
            all.AddRange(passcode);
            PasscodeWarning = string.Join("\n", passcode.Select(d => d.Message));
            Diagnostics.Clear();
            foreach (var d in all.OrderByDescending(d => d.Severity))
                Diagnostics.Add(new DiagnosticItem(d.Severity, d.ToString().Replace("[Error] ", "").Replace("[Warning] ", "").Replace("[Info] ", "")));
            ErrorCount = all.Count(d => d.Severity == Severity.Error);
            WarningCount = all.Count(d => d.Severity == Severity.Warning);

            for (int i = 0; i < Effects.Count; i++)
            {
                var vm = Effects[i];
                vm.Number = i + 1;
                vm.PreviewText = EffectTextGenerator.Describe(vm.Model, C, pack);
                vm.Problems = string.Join("\n", result.Diagnostics.Where(d => d.EffectIndex == i + 1).Select(d => (d.Severity == Severity.Error ? "✖ " : "⚠ ") + d.Message));
            }
            SuggestedText = EffectTextGenerator.Generate(Project, pack);
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(NoProblems));
            OnPropertyChanged(nameof(StatusIcon));
            OnPropertyChanged(nameof(ListLabel));
            OnPropertyChanged(nameof(ListDetail));
            OnPropertyChanged(nameof(HasNoEffects));
            OnPropertyChanged(nameof(TypeLine));
            OnPropertyChanged(nameof(FaceText));
            FaceVersion++;
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---- passcode
    [ObservableProperty] private string _passcodeWarning = "";
    public bool HasPasscodeWarning => PasscodeWarning.Length > 0;
    partial void OnPasscodeWarningChanged(string value) => OnPropertyChanged(nameof(HasPasscodeWarning));

    private List<Diagnostic> PasscodeProblems(CardPack pack)
    {
        var list = new List<Diagnostic>();
        var others = pack.Cards.Where(p => !ReferenceEquals(p, Project)).ToList();
        if (others.FirstOrDefault(o => o.Card.Id == C.Id) is { } dup)
            list.Add(new(Severity.Error, $"Passcode {C.Id} is also used by \"{dup.Card.Name}\" in this pack. Click Generate for a free one."));
        if (_catalog().Clash(C.Id) is { } used)
            list.Add(new(Severity.Warning, $"Passcode {C.Id} is already used in Omega by {used}. Click Generate for a free one."));
        if (others.FirstOrDefault(o => PasscodeGenerator.TooClose(o.Card.Id, C.Id)) is { } close)
            list.Add(new(Severity.Info, $"Passcode is close to \"{close.Card.Name}\" ({close.Card.Id}); their once-per-turn limits can interfere. Generate keeps cards apart."));
        return list;
    }

    [RelayCommand]
    private void GeneratePasscode() => Id = _newPasscode(C.Id);

    // ---- card face preview
    /// <summary>Bumped on every change so the preview knows to redraw.</summary>
    [ObservableProperty] private int _faceVersion;
    public string TypeLine => CardFaceText.TypeLine(C, _pack());
    public string FaceText => string.IsNullOrWhiteSpace(C.Text) ? SuggestedText : C.Text;
    public CardData Data => C;

    // ---- card data
    public string Name { get => C.Name; set { if (C.Name == value) return; C.Name = value; OnPropertyChanged(); Changed(); } }

    public decimal? Id
    {
        get => C.Id;
        set { if (C.Id == (long)(value ?? 0)) return; C.Id = (long)(value ?? 0); OnPropertyChanged(); Changed(); }
    }

    public IReadOnlyList<Option<CardKind>> KindOptions => Lists.Kinds;
    public Option<CardKind> Kind
    {
        get => Options.Find(Lists.Kinds, C.Kind);
        set { if (value is null || Equals(C.Kind, value.Value)) return; C.Kind = value.Value; NotifyShape(); Changed(); }
    }

    public IReadOnlyList<Option<MonsterFrame>> FrameOptions => Lists.Frames;
    public Option<MonsterFrame> Frame
    {
        get => Options.Find(Lists.Frames, C.Frame);
        set
        {
            if (value is null || value.Value == C.Frame) return;
            C.Frame = value.Value;
            Project.Procedure.Kind = value.Value switch
            {
                MonsterFrame.Fusion => SummonProcedureKind.Fusion,
                MonsterFrame.Synchro => SummonProcedureKind.Synchro,
                MonsterFrame.Xyz => SummonProcedureKind.Xyz,
                MonsterFrame.Link => SummonProcedureKind.Link,
                _ => SummonProcedureKind.None,
            };
            if (value.Value == MonsterFrame.Synchro && Project.Procedure.MinMaterials > 1) Project.Procedure.MinMaterials = 1;
            NotifyShape();
            Changed();
        }
    }

    public IReadOnlyList<Option<SpellKind>> SpellKindOptions => Lists.SpellKinds;
    public Option<SpellKind> SpellKind
    {
        get => Options.Find(Lists.SpellKinds, C.SpellKind);
        set { if (value is null || Equals(C.SpellKind, value.Value)) return; C.SpellKind = value.Value; OnPropertyChanged(); Changed(); }
    }

    public IReadOnlyList<Option<TrapKind>> TrapKindOptions => Lists.TrapKinds;
    public Option<TrapKind> TrapKind
    {
        get => Options.Find(Lists.TrapKinds, C.TrapKind);
        set { if (value is null || Equals(C.TrapKind, value.Value)) return; C.TrapKind = value.Value; OnPropertyChanged(); Changed(); }
    }

    public IReadOnlyList<Option<CardAttribute>> AttributeOptions => Lists.Attributes;
    public Option<CardAttribute> Attribute
    {
        get => Options.Find(Lists.Attributes, C.Attribute);
        set { if (value is null || Equals(C.Attribute, value.Value)) return; C.Attribute = value.Value; OnPropertyChanged(); Changed(); }
    }

    private IReadOnlyList<Option<CardRace>>? _raceOptions;
    public IReadOnlyList<Option<CardRace>> RaceOptions => _raceOptions ??= Lists.RacesFor(_pack(), C.Race);
    public Option<CardRace> Race
    {
        get => Options.Find(RaceOptions, C.Race);
        set { if (value is null || Equals(C.Race, value.Value)) return; C.Race = value.Value; OnPropertyChanged(); Changed(); }
    }

    public decimal? Level { get => C.Level; set { if (C.Level == (int)(value ?? 0)) return; C.Level = (int)(value ?? 0); OnPropertyChanged(); Changed(); } }
    public string LevelLabel => C.IsXyz ? "Rank" : "Level";

    /// <summary>ATK and DEF as text so "?" can be typed.</summary>
    public string Atk { get => StatText(C.Atk); set { if (C.Atk == ParseStat(value)) return; C.Atk = ParseStat(value); OnPropertyChanged(); Changed(); } }
    public string Def { get => StatText(C.Def); set { if (C.Def == ParseStat(value)) return; C.Def = ParseStat(value); OnPropertyChanged(); Changed(); } }
    private static string StatText(int v) => v == -2 ? "?" : v.ToString(CultureInfo.InvariantCulture);
    private static int ParseStat(string s) =>
        s.Trim() == "?" ? -2 : int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 999999) : 0;

    public decimal? LeftScale { get => C.LeftScale; set { if (C.LeftScale == (int)(value ?? 0)) return; C.LeftScale = (int)(value ?? 0); OnPropertyChanged(); Changed(); } }
    public decimal? RightScale { get => C.RightScale; set { if (C.RightScale == (int)(value ?? 0)) return; C.RightScale = (int)(value ?? 0); OnPropertyChanged(); Changed(); } }

    private bool Ability(MonsterAbility a) => C.Abilities.HasFlag(a);
    private void SetAbility(MonsterAbility a, bool on, string name)
    {
        if (Ability(a) == on) return;
        C.Abilities = on ? C.Abilities | a : C.Abilities & ~a;
        OnPropertyChanged(name);
        NotifyShape();
        Changed();
    }

    public bool IsTuner { get => Ability(MonsterAbility.Tuner); set => SetAbility(MonsterAbility.Tuner, value, nameof(IsTuner)); }
    public bool IsFlip { get => Ability(MonsterAbility.Flip); set => SetAbility(MonsterAbility.Flip, value, nameof(IsFlip)); }
    public bool IsSpirit { get => Ability(MonsterAbility.Spirit); set => SetAbility(MonsterAbility.Spirit, value, nameof(IsSpirit)); }
    public bool IsToon { get => Ability(MonsterAbility.Toon); set => SetAbility(MonsterAbility.Toon, value, nameof(IsToon)); }
    public bool IsUnion { get => Ability(MonsterAbility.Union); set => SetAbility(MonsterAbility.Union, value, nameof(IsUnion)); }
    public bool IsGemini { get => Ability(MonsterAbility.Gemini); set => SetAbility(MonsterAbility.Gemini, value, nameof(IsGemini)); }
    public bool IsPendulumAbility { get => Ability(MonsterAbility.Pendulum); set => SetAbility(MonsterAbility.Pendulum, value, nameof(IsPendulumAbility)); }

    // ---- YGO Omega's card pool and limitation tags
    /// <summary>"Same as the pack" plus each pool. The card's own pool is stored only when it differs from the pack's.</summary>
    public IReadOnlyList<Option<long>> CardPoolOptions =>
        new[] { new Option<long>(0, $"Same as the pack ({Lists.PoolName(_pack().Ot)})") }
            .Concat(Lists.Pools.Select(p => new Option<long>((long)p.Value, p.Label))).ToList();

    public Option<long> CardPoolChoice
    {
        get => CardPoolOptions.FirstOrDefault(o => o.Value == (C.Ot ?? 0)) ?? CardPoolOptions[0];
        set
        {
            var ot = value is null || value.Value == 0 || value.Value == _pack().Ot ? (long?)null : value.Value;
            if (ot == C.Ot) return;
            C.Ot = ot;
            // Release dates follow the pool: Omega lists OCG/TCG cards by them.
            C.OcgDate = C.TcgDate = null;
            OnPropertyChanged();
            Changed();
        }
    }

    public void OnPackPoolChanged()
    {
        if (C.Ot == _pack().Ot) C.Ot = null;
        OnPropertyChanged(nameof(CardPoolOptions));
        OnPropertyChanged(nameof(CardPoolChoice));
    }

    public IReadOnlyList<Option<Limitation>> LimitationOptions => Lists.Limitations;
    public Option<Limitation> LimitationChoice
    {
        get => Options.Find(Lists.Limitations, C.Limitation);
        set
        {
            if (value is null || (value.Value == C.Limitation && C.BanlistLimits.Count == 0)) return;
            C.Limitation = value.Value;
            C.BanlistLimits.Clear();
            OnPropertyChanged();
            Changed();
        }
    }

    // ---- summoning rules
    /// <summary>The choices that make sense for this frame. Extra Deck and Ritual monsters already can't be Normal Summoned.</summary>
    public IReadOnlyList<Option<SummonRule>> SummonRuleOptions => C.IsMainDeckNonRitual
        ? Options.Of(
            (SummonRule.Normal, "Can be Normal Summoned"),
            (SummonRule.SpecialSummonOnly, "Can't be Normal Summoned: must first be Special Summoned"),
            (SummonRule.ProcedureOnly, "Can only be Special Summoned by its own procedure"))
        : Options.Of(
            (SummonRule.Normal, $"{C.Frame} Summon, can be revived once properly Summoned"),
            (SummonRule.ProcedureOnly, $"Must be {C.Frame} Summoned: can't be Special Summoned other ways"));

    public Option<SummonRule> SummonRuleChoice
    {
        get => Options.Find(SummonRuleOptions, C.IsMainDeckNonRitual || C.SummonRule == SummonRule.ProcedureOnly ? C.SummonRule : SummonRule.Normal);
        set
        {
            if (value is null || value.Value == C.SummonRule) return;
            C.SummonRule = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummonRuleHelp));
            NotifyShape();
            Changed();
        }
    }

    public string SummonRuleHelp => (C.IsMainDeckNonRitual, C.SummonRule) switch
    {
        (true, SummonRule.Normal) => "To also let it Special Summon itself, like Cyber Dragon, add an effect with the WHEN block \"Special Summon procedure\".",
        (true, _) => "Say how it is Special Summoned with an effect that uses the WHEN block \"Special Summon procedure\" (Effects tab, or the \"Summon procedure\" templates).",
        _ => "",
    };

    private bool Arrow(LinkMarker m) => C.LinkMarkers.HasFlag(m);
    private void SetArrow(LinkMarker m, bool on, string name)
    {
        if (Arrow(m) == on) return;
        C.LinkMarkers = on ? C.LinkMarkers | m : C.LinkMarkers & ~m;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(LinkRatingText));
        OnPropertyChanged(nameof(LinkRatingShort));
        Changed();
    }

    public bool ArrowTopLeft { get => Arrow(LinkMarker.TopLeft); set => SetArrow(LinkMarker.TopLeft, value, nameof(ArrowTopLeft)); }
    public bool ArrowTop { get => Arrow(LinkMarker.Top); set => SetArrow(LinkMarker.Top, value, nameof(ArrowTop)); }
    public bool ArrowTopRight { get => Arrow(LinkMarker.TopRight); set => SetArrow(LinkMarker.TopRight, value, nameof(ArrowTopRight)); }
    public bool ArrowLeft { get => Arrow(LinkMarker.Left); set => SetArrow(LinkMarker.Left, value, nameof(ArrowLeft)); }
    public bool ArrowRight { get => Arrow(LinkMarker.Right); set => SetArrow(LinkMarker.Right, value, nameof(ArrowRight)); }
    public bool ArrowBottomLeft { get => Arrow(LinkMarker.BottomLeft); set => SetArrow(LinkMarker.BottomLeft, value, nameof(ArrowBottomLeft)); }
    public bool ArrowBottom { get => Arrow(LinkMarker.Bottom); set => SetArrow(LinkMarker.Bottom, value, nameof(ArrowBottom)); }
    public bool ArrowBottomRight { get => Arrow(LinkMarker.BottomRight); set => SetArrow(LinkMarker.BottomRight, value, nameof(ArrowBottomRight)); }
    public string LinkRatingText => $"LINK-{C.LinkRating}";
    /// <summary>The rating alone, shown in the middle of the Link Arrow picker.</summary>
    public string LinkRatingShort => C.LinkRating.ToString(CultureInfo.InvariantCulture);

    public string Text { get => C.Text; set { if (C.Text == value) return; C.Text = value; OnPropertyChanged(); Changed(); } }
    public string PendulumText { get => C.PendulumText; set { if (C.PendulumText == value) return; C.PendulumText = value; OnPropertyChanged(); Changed(); } }

    // ---- what to show for this kind of card
    public bool IsMonster => C.IsMonster;
    public bool IsSpell => C.Kind == CardKind.Spell;
    public bool IsTrap => C.Kind == CardKind.Trap;
    public bool IsLink => C.IsLink;
    public bool ShowLevel => C.IsMonster && !C.IsLink;
    public bool ShowDef => C.IsMonster && !C.IsLink;
    public bool IsPendulum => C.IsPendulum;
    public bool IsExtraDeck => C.IsExtraDeck;
    public bool IsFusion => C.IsMonster && C.Frame == MonsterFrame.Fusion;
    public bool IsSynchro => C.IsMonster && C.Frame == MonsterFrame.Synchro;
    public bool ShowMaterialFilter => C.IsExtraDeck && !(IsFusion && Project.Procedure.FusionMaterialCodes.Count >= 2);
    public string ProcedureTitle => C.Frame switch
    {
        MonsterFrame.Fusion => "Fusion materials",
        MonsterFrame.Synchro => "Synchro materials (1 Tuner + non-Tuners)",
        MonsterFrame.Xyz => $"Xyz materials (Level {C.Level} monsters)",
        MonsterFrame.Link => "Link materials",
        _ => "",
    };
    public string MinMaterialsLabel => IsSynchro ? "Non-Tuners at least" : "Materials at least";

    private void NotifyShape()
    {
        foreach (var n in new[]
        {
            nameof(Kind), nameof(Frame), nameof(IsMonster), nameof(IsSpell), nameof(IsTrap), nameof(IsLink), nameof(ShowLevel),
            nameof(ShowDef), nameof(IsPendulum), nameof(IsExtraDeck), nameof(IsFusion), nameof(IsSynchro), nameof(ShowMaterialFilter),
            nameof(ProcedureTitle), nameof(MinMaterialsLabel), nameof(LevelLabel), nameof(LinkRatingText),
            nameof(SummonRuleOptions), nameof(SummonRuleChoice), nameof(SummonRuleHelp),
        }) OnPropertyChanged(n);
    }

    // ---- summon procedure
    public FilterEditorViewModel MaterialFilter { get; }
    public FilterEditorViewModel TunerFilter { get; }
    public decimal? MinMaterials { get => Project.Procedure.MinMaterials; set { if (Project.Procedure.MinMaterials == (int)(value ?? 1)) return; Project.Procedure.MinMaterials = (int)(value ?? 1); OnPropertyChanged(); Changed(); } }
    public decimal? MaxMaterials { get => Project.Procedure.MaxMaterials; set { if (Project.Procedure.MaxMaterials == (int)(value ?? 0)) return; Project.Procedure.MaxMaterials = (int)(value ?? 0); OnPropertyChanged(); Changed(); } }

    /// <summary>Fusion materials by passcode, comma separated.</summary>
    public string FusionCodes
    {
        get => string.Join(", ", Project.Procedure.FusionMaterialCodes);
        set
        {
            if (value == FusionCodes) return;
            Project.Procedure.FusionMaterialCodes = value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0)
                .Where(v => v > 0).ToList();
            OnPropertyChanged(nameof(ShowMaterialFilter));
            Changed();
        }
    }

    // ---- archetypes
    public ObservableCollection<ArchetypeChoice> ArchetypeChoices { get; } = [];
    public bool HasArchetypes => ArchetypeChoices.Count > 0;

    public void RebuildTypes()
    {
        _raceOptions = null;
        OnPropertyChanged(nameof(RaceOptions));
        OnPropertyChanged(nameof(Race));
        foreach (var e in Effects) e.RefreshFilters();
        MaterialFilter.RefreshArchetypes();
        TunerFilter.RefreshArchetypes();
    }

    public void RebuildArchetypes()
    {
        ArchetypeChoices.Clear();
        foreach (var a in _pack().Archetypes)
            ArchetypeChoices.Add(new ArchetypeChoice(a, C.Archetypes.Contains(a.Code), OnArchetypeToggled));
        OnPropertyChanged(nameof(HasArchetypes));
        foreach (var e in Effects) e.RefreshFilters();
        MaterialFilter.RefreshArchetypes();
        TunerFilter.RefreshArchetypes();
    }

    private void OnArchetypeToggled(ArchetypeChoice choice)
    {
        C.Archetypes.Remove(choice.Archetype.Code);
        if (choice.IsSelected) C.Archetypes.Add(choice.Archetype.Code);
        Changed();
    }

    // ---- artwork
    public string? ArtworkPath
    {
        get => Project.ArtworkPath;
        set { if (Project.ArtworkPath == value) return; Project.ArtworkPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasArtwork)); Changed(); }
    }
    public bool HasArtwork => !string.IsNullOrWhiteSpace(Project.ArtworkPath);
    public string? PackFolder { get; set; }
    public string? ResolvedArtwork => Core.Omega.PackExporter.ResolveArtwork(Project.ArtworkPath, PackFolder);

    // ---- effects
    public bool HasNoEffects => Effects.Count == 0;
    public IReadOnlyList<EffectTemplate> Templates => EffectTemplates.All;

    [RelayCommand]
    private void AddEffect()
    {
        var model = new EffectEvent { Label = $"Effect {Effects.Count + 1}" };
        model.When = Core.Blocks.BlockRegistry.Find(C.IsMonster ? "when.ignition" : "when.activate")!.CreateInstance();
        AddEffectModel(model);
    }

    public void AddTemplate(EffectTemplate template) => AddEffectModel(template.Create());

    private void AddEffectModel(EffectEvent model)
    {
        Project.Effects.Add(model);
        Effects.Add(new EffectViewModel(model, this, _pack, Changed));
        Changed();
    }

    public void RemoveEffect(EffectViewModel vm)
    {
        Project.Effects.Remove(vm.Model);
        Effects.Remove(vm);
        Changed();
    }

    public void MoveEffect(EffectViewModel vm, int delta)
    {
        var i = Effects.IndexOf(vm);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Effects.Count) return;
        Effects.Move(i, j);
        (Project.Effects[i], Project.Effects[j]) = (Project.Effects[j], Project.Effects[i]);
        Changed();
    }

    public void DuplicateEffect(EffectViewModel vm)
    {
        var copy = PackSerializer.Clone(vm.Model);
        copy.Label += " (copy)";
        var i = Effects.IndexOf(vm) + 1;
        Project.Effects.Insert(i, copy);
        Effects.Insert(i, new EffectViewModel(copy, this, _pack, Changed));
        Changed();
    }

    [RelayCommand]
    private void UseSuggestedText()
    {
        Text = SuggestedText;
    }

    // ---- hand-written script
    public bool UseCustomLua
    {
        get => Project.UseCustomLua;
        set { if (Project.UseCustomLua == value) return; Project.UseCustomLua = value; OnPropertyChanged(); OnPropertyChanged(nameof(UsesBlocks)); Changed(); }
    }
    public bool UsesBlocks => !Project.UseCustomLua;

    public string CustomLua
    {
        get => Project.CustomLua;
        set { if (Project.CustomLua == value) return; Project.CustomLua = value; OnPropertyChanged(); Changed(); }
    }

    /// <summary>Copies the generated script into the editable script box and switches to it.</summary>
    [RelayCommand]
    private void EjectToLua()
    {
        CustomLua = ScriptCompiler.Compile(new CardProject { Card = C, Procedure = Project.Procedure, Effects = Project.Effects }, _pack()).Lua;
        UseCustomLua = true;
    }

    [RelayCommand]
    private void BackToBlocks() => UseCustomLua = false;
}
