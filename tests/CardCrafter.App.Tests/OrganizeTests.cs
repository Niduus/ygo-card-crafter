using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.Tests;

/// <summary>The Organize button on the Effects tab with its Undo, and the archetype line's note and fix on the Card tab.</summary>
public class OrganizeTabTests
{
    private static (MainWindow Window, MainViewModel Vm) Open()
    {
        var window = new MainWindow { Width = 1360, Height = 900 };
        var vm = new MainViewModel(new FakeDialogs(), new AppSettings());
        window.DataContext = vm;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static void SelectTab(Window window, string header)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => (string?)t.Header == header);
        Dispatcher.UIThread.RunJobs();
    }

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static Button Find(Window window, string name) => window.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);

    [AvaloniaFact]
    public void Organize_ReordersTheEffects_AndUndoPutsThemBack()
    {
        var (window, vm) = Open();
        var card = vm.SelectedCard!;
        card.Project.Effects.Clear();
        card.Project.Effects.Add(new EffectEvent { Label = "GY", When = B("when.ignition", ("zone", "graveyard")), Do = [B("do.draw")] });
        card.Project.Effects.Add(new EffectEvent { Label = "On Summon", When = B("when.summoned", ("how", "normal")), Do = [B("do.draw")] });
        card.ReloadFromProject();
        SelectTab(window, "Effects");

        var organize = Find(window, "OrganizeButton");
        Assert.True(organize.IsEffectivelyVisible);
        organize.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["On Summon", "GY"], card.Effects.Select(e => e.Label));
        Assert.Equal(["On Summon", "GY"], card.Project.Effects.Select(e => e.Label));
        Assert.Contains("order cards print them", card.OrganizeReport);
        var undo = Find(window, "UndoOrganizeButton");
        Assert.True(undo.IsEffectivelyVisible);

        undo.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["GY", "On Summon"], card.Effects.Select(e => e.Label));
        Assert.False(card.CanUndoOrganize);

        // After another change, Undo isn't offered: it would throw that change away.
        card.OrganizeCommand.Execute(null);
        Assert.True(card.CanUndoOrganize);
        card.Effects[0].Label = "Renamed";
        Assert.False(card.CanUndoOrganize);
    }

    [AvaloniaFact]
    public void ArchetypeLine_TheNoteAndItsFix()
    {
        var (window, vm) = Open();
        var card = vm.SelectedCard!;
        vm.Pack.Archetypes.Add(new Archetype { Code = 0xa4, Name = "Kuriboh" });
        card.RebuildArchetypes();
        card.ArchetypeChoices.First(a => a.Archetype.Code == 0xa4).IsSelected = true;
        Assert.StartsWith("(This card is always treated as a \"Kuriboh\" card.)\n", card.SuggestedText);

        card.Text = "This card gains 100 ATK.";
        Assert.True(card.HasArchetypeReminderNote);
        SelectTab(window, "Card");
        var fix = Find(window, "AddArchetypeReminderButton");
        Assert.True(fix.IsEffectivelyVisible);
        fix.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("(This card is always treated as a \"Kuriboh\" card.)\nThis card gains 100 ATK.", card.Text);
        Assert.False(card.HasArchetypeReminderNote);

        // Turned off: no line in the suggested text, and no note.
        card.Text = "This card gains 100 ATK.";
        card.ArchetypeReminder = false;
        Assert.False(card.HasArchetypeReminderNote);
        Assert.DoesNotContain("always treated", card.SuggestedText);
    }
}
