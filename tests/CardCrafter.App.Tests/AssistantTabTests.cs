using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Assistant;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.Tests;

public class AssistantTabTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [AvaloniaFact]
    public void ClaudesChanges_ShowInTheEditor_AndCanBeUndone()
    {
        var vm = new MainViewModel(new FakeDialogs(), new AppSettings());
        var tools = new AssistantTools(vm);
        var before = vm.SnapshotCards();

        Assert.False(tools.Run("update_card", J("""{"card":{"name":"Ember Knight","atk":2100}}""")).IsError);
        Assert.Equal("Ember Knight", vm.SelectedCard!.Name);
        Assert.Equal("2100", vm.SelectedCard.Atk);
        Assert.True(vm.IsDirty);
        Assert.False(tools.Run("new_card", J("""{"kind":"spell"}""")).IsError);
        Assert.Equal(2, vm.Cards.Count);

        Assert.True(vm.RestoreCards(before));
        Assert.Single(vm.Cards);
        Assert.Equal("My First Card", vm.SelectedCard!.Name);
        Assert.Equal("1600", vm.SelectedCard.Atk);
    }

    [AvaloniaFact]
    public void TheTab_ShowsSetupAndStarters()
    {
        var window = new MainWindow { Width = 1360, Height = 900 };
        var vm = new MainViewModel(new FakeDialogs(), new AppSettings());
        window.DataContext = vm;
        window.Show();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => (string?)t.Header == "Assistant");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Assistant.IsEmpty);
        Assert.Equal(AssistantModel.Default, vm.Assistant.SelectedModel);
        vm.Assistant.SuggestCommand.Execute("Check this card for problems.");
        Assert.Equal("Check this card for problems.", vm.Assistant.Input);
        var dir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "cardcrafter-shots");
        Directory.CreateDirectory(dir);
        window.UpdateLayout();
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, "assistant-tab.png"));
    }
}
