using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Templates;

namespace CardCrafter.App.Tests;

public class MainWindowTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "cardcrafter-shots");

    private static (MainWindow Window, MainViewModel Vm) Open()
    {
        var window = new MainWindow { Width = 1360, Height = 900 };
        var vm = new MainViewModel(new FakeDialogs(), new AppSettings());
        window.DataContext = vm;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static void Shot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var frame = window.CaptureRenderedFrame();
        Directory.CreateDirectory(ShotDir);
        frame?.Save(Path.Combine(ShotDir, name + ".png"));
    }

    private static void SelectTab(Window window, string header)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => (string?)t.Header == header);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void StartsWithAStarterCard()
    {
        var (window, vm) = Open();
        Assert.Single(vm.Cards);
        Assert.Equal("My First Card", vm.SelectedCard!.Name);
        Assert.False(vm.SelectedCard.HasErrors, string.Join("\n", vm.SelectedCard.Diagnostics.Select(d => d.Text)));
        Shot(window, "1-card-tab");
    }

    [AvaloniaFact]
    public void EffectsTab_HandWrittenScript_OffersBlocks()
    {
        var (window, vm) = Open();
        var card = vm.SelectedCard!;
        var effects = card.Effects.Count;
        card.EjectToLuaCommand.Execute(null);
        SelectTab(window, "Effects");
        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "TranslateScriptButton");
        Assert.True(button.IsEffectivelyVisible);
        Shot(window, "18-effects-script-to-blocks");
        card.TranslateScriptCommand.Execute(null);
        // Either the script became blocks again, or it stays script and the report says which effects have none.
        if (card.UsesBlocks) Assert.Equal(effects, card.Effects.Count);
        else Assert.Contains("\u2022", card.TranslationReport);
    }

    [AvaloniaFact]
    public void CardTab_SummoningConditions_FusionLinkAndEquip()
    {
        var (window, vm) = Open();
        var card = vm.SelectedCard!;
        vm.Pack.Archetypes.Add(new Archetype { Code = 0x10f4, Name = "Aleister" });
        card.RebuildArchetypes();

        card.Frame = card.FrameOptions.First(o => o.Value == MonsterFrame.Fusion);
        // An older-style single filter becomes one slot; make it Invoked's two materials.
        Assert.Single(card.FusionSlots);
        card.FusionSlots[0].Min = 1;
        card.FusionSlots[0].Max = 1;
        card.FusionSlots[0].Filter.Archetype = card.FusionSlots[0].Filter.ArchetypeOptions.First(a => a.Code == 0x10f4);
        card.AddFusionSlotCommand.Execute(null);
        card.FusionSlots[1].Filter.AttrLight = true;
        card.FusionSlots[1].Filter.AttrEarth = true;
        Assert.Equal("1 \"Aleister\" monster + 1 LIGHT or EARTH monster", card.MaterialsText);
        Assert.False(card.HasErrors, string.Join("\n", card.Diagnostics.Select(d => d.Text)));
        Shot(window, "19-card-fusion-materials");

        card.Frame = card.FrameOptions.First(o => o.Value == MonsterFrame.Link);
        card.ArrowLeft = card.ArrowRight = true;
        card.MaterialFilter.MonsterKind = card.MaterialFilter.MonsterKindOptions.First(o => o.Value == MonsterKind.Effect);
        card.MinMaterials = 2;
        Assert.Equal("2 Effect Monsters", card.MaterialsText);
        Assert.StartsWith("2 Effect Monsters", card.SuggestedText);
        Shot(window, "20-card-link-materials");

        card.Kind = card.KindOptions.First(o => o.Value == CardKind.Spell);
        card.SpellKind = card.SpellKindOptions.First(o => o.Value == SpellKind.Equip);
        Assert.True(card.IsEquipSpell);
        card.EquipFilter.Race = card.EquipFilter.RaceOptions.First(o => o.Value == CardRace.Warrior);
        Assert.Contains("Equip only to a Warrior monster.", card.SuggestedText);
        Shot(window, "21-card-equip");

        // The filter pop-up with its new Monster kind and Attribute boxes.
        var chip = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "EquipFilterButton");
        chip.Flyout!.ShowAt(chip);
        Shot(window, "22-filter-editor");
    }

    [AvaloniaFact]
    public void CardTab_LinkPendulumAndSpellLayouts()
    {
        var (window, vm) = Open();
        var card = vm.SelectedCard!;
        card.Frame = card.FrameOptions.First(o => o.Value == MonsterFrame.Link);
        card.ArrowBottomLeft = card.ArrowBottom = card.ArrowBottomRight = true;
        Shot(window, "13-card-link");
        Assert.Equal("3", card.LinkRatingShort);
        card.Frame = card.FrameOptions.First(o => o.Value == MonsterFrame.Effect);
        card.IsPendulumAbility = true;
        Shot(window, "14-card-pendulum");
        card.Kind = card.KindOptions.First(o => o.Value == CardKind.Spell);
        Shot(window, "15-card-spell");
    }

    [AvaloniaFact]
    public void SummonProcedure_HasNoDoBlocks_AndSetsTheCardText()
    {
        var (window, vm) = Open();
        var card = vm.SelectedCard!;
        card.SummonRuleChoice = card.SummonRuleOptions.First(o => o.Value == SummonRule.SpecialSummonOnly);
        card.AddTemplate(EffectTemplates.All.First(t => t.Name == "Banish from GY to summon"));
        var effect = card.Effects.Last();
        Assert.True(effect.IsProcedure);
        Assert.Empty(effect.Actions);
        Assert.False(card.HasErrors, string.Join("\n", card.Diagnostics.Select(d => d.Text)));
        Assert.StartsWith("Cannot be Normal Summoned/Set. Must first be Special Summoned (from your hand) by banishing 2 monsters", effect.PreviewText);
        Assert.Contains("EFFECT_SPSUMMON_PROC", card.Lua);
        SelectTab(window, "Effects");
        Shot(window, "12-summon-procedure");
    }

    [AvaloniaFact]
    public void EventSheetRendersAndEditsCompileLive()
    {
        var (window, vm) = Open();
        var card = vm.SelectedCard!;
        card.AddTemplate(EffectTemplates.All.First(t => t.Name == "Quick banish from GY"));
        var effect = card.Effects[0];
        effect.AddBlock(BlockRegistry.Find("if.turn")!);
        effect.AddBlock(BlockRegistry.Find("do.damage")!);
        SelectTab(window, "Effects");
        Shot(window, "2-effects-tab");

        // The sheet shows one block row per placed block.
        // Starter effect: WHEN (Normal Summoned) + or WHEN (Special Summoned) + IF + 2 DO. Template effect: WHEN + DO.
        Assert.Equal(7, window.GetVisualDescendants().OfType<BlockView>().Count());

        // Changing a blank recompiles the script immediately.
        var damage = card.Effects[0].Actions.Last();
        var amount = damage.Segments.OfType<IntSegmentViewModel>().First();
        amount.Value = 1234;
        Assert.Contains("Duel.Damage(1-tp,1234,REASON_EFFECT)", card.Lua);
        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public void ShowingEveryTab_DoesNotMarkThePackChanged()
    {
        var (window, vm) = Open();
        vm.SelectedCard!.AddTemplate(EffectTemplates.All.First(t => t.Name == "Quick banish from GY"));
        vm.IsDirty = false;
        foreach (var tab in new[] { "Card", "Effects", "Script", "Omega", "Effects" }) SelectTab(window, tab);
        Assert.False(vm.IsDirty);
    }

    [AvaloniaFact]
    public void TemplateFlagsShowAsTicked()
    {
        var (_, vm) = Open();
        vm.SelectedCard!.AddTemplate(EffectTemplates.All.First(t => t.Name == "Quick banish from GY"));
        var banish = vm.SelectedCard.Effects.Last().Actions.Single();
        Assert.True(banish.Segments.OfType<BoolSegmentViewModel>().Single(s => s.Label == "(targeting)").Value);
    }

    [AvaloniaFact]
    public void ScriptTabShowsGeneratedLua()
    {
        var (window, vm) = Open();
        SelectTab(window, "Script");
        Shot(window, "3-script-tab");
        Assert.Contains("function s.initial_effect(c)", vm.SelectedCard!.Lua);
        Assert.True(vm.SelectedCard.NoProblems);
    }

    [AvaloniaFact]
    public void OmegaTabRenders()
    {
        var (window, vm) = Open();
        vm.ScanReport = "YGO Card Crafter - Omega folder scan\nFolder: C:\\Program Files (x86)\\YGO Omega\n...";
        vm.OmegaStatus = "YGO Omega: C:\\Program Files (x86)\\YGO Omega (found via the Windows uninstall registry).";
        SelectTab(window, "Omega");
        Shot(window, "4-omega-tab");
    }

    [AvaloniaFact]
    public void FilterEditorRenders()
    {
        var pack = new CardPack();
        pack.Archetypes.Add(new Archetype { Code = 0xa00, Name = "Commander" });
        var filter = new CardFilter { Zones = Zone.Deck | Zone.Graveyard, CardType = FilterCardType.Monster, Race = CardRace.Dragon, LevelMax = 4 };
        var changed = 0;
        var vm = new FilterEditorViewModel(filter, Zone.None, false, () => pack, () => changed++);
        var window = new Window { Width = 420, Height = 560, Content = new FilterEditorView { DataContext = vm, Margin = new Avalonia.Thickness(16) } };
        window.Show();
        Shot(window, "5-filter-editor");
        Assert.Equal("Level 4 or lower Dragon monster from your Deck or GY", vm.Summary);
        vm.InHand = true;
        Assert.Equal(Zone.Deck | Zone.Graveyard | Zone.Hand, filter.Zones);
        Assert.Equal(1, changed);
    }

    [AvaloniaFact]
    public void ExtraDeckMonsterShowsProcedure()
    {
        var (window, vm) = Open();
        var card = vm.SelectedCard!;
        card.Frame = Lists.Frames.First(f => f.Value == MonsterFrame.Link);
        card.ArrowBottomLeft = true;
        card.ArrowBottomRight = true;
        Assert.Equal("LINK-2", card.LinkRatingText);
        Assert.Contains("aux.AddLinkProcedure(c,nil,2)", card.Lua);
        Shot(window, "6-link-monster");
    }

    [AvaloniaFact]
    public void SaveAndReopenRoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("cc-app-").FullName;
        var path = Path.Combine(dir, "pack.ycc");
        var dialogs = new FakeDialogs { NextFile = path };
        var vm = new MainViewModel(dialogs, new AppSettings());
        vm.AddCardCommand.Execute("Spell");
        vm.SelectedCard!.Name = "Saved Spell";
        Assert.True(vm.SavePackAsCommand.ExecuteAsync(null).Wait(5000));
        Assert.False(vm.IsDirty);

        var again = new MainViewModel(new FakeDialogs(), new AppSettings());
        Assert.True(again.OpenPath(path));
        Assert.Equal(2, again.Cards.Count);
        Assert.Equal("Saved Spell", again.Cards[1].Name);
        Directory.Delete(dir, true);
    }
}
