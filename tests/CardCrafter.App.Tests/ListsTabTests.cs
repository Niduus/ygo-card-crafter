using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.Tests;

public class ListsTabTests
{
    [AvaloniaFact]
    public async Task ListsTab_ArchetypesTypesAndBanlists()
    {
        using var tmp = new TempDirectory();
        var omega = Path.Combine(tmp.Path, "YGO Omega");
        var files = Path.Combine(omega, "YGO Omega_Data", "Files");
        Directory.CreateDirectory(Path.Combine(files, "Scripts"));
        File.WriteAllText(Path.Combine(omega, "YGO Omega.exe"), "");
        File.WriteAllText(Path.Combine(files, "Scripts", "c1.lua"), "");
        var rows = new List<CdbRow>();
        long id = 1000;
        foreach (var bit in new long[] { 0x4000000, 0x8000000, 0x10000000, 0x20000000, 0x40000000, 0x80000000 })
            rows.Add(CdbEncoder.Encode(new CardData { Id = id++, Name = $"Some Very Long Rush Duel Monster Name {id}", Race = (CardRace)bit }, [], 3));
        rows.Add(CdbEncoder.Encode(new CardData { Id = id++, Name = "Elemental HERO Neos", Archetypes = [0x3008] }, [], 3));
        rows.Add(CdbEncoder.Encode(new CardData { Id = id++, Name = "Elemental HERO Avian", Archetypes = [0x3008] }, [], 3));
        rows.Add(CdbEncoder.Encode(new CardData { Id = 55144522, Name = "Pot of Greed", Kind = CardKind.Spell }, [], 3));
        var db = Path.Combine(files, "Databases", "Rush.db");
        CdbFile.WriteFresh(db, rows);
        // A banlist the way Omega keeps them.
        CdbFile.WriteBanlists(db, [new PackBanlist { Id = 1, Name = "TCG", Entries = [new BanlistEntry { Code = 55144522, Limit = 0 }] }]);

        var vm = new MainViewModel(new FakeDialogs(), new AppSettings { OmegaPath = omega });
        var window = new MainWindow { Width = 1360, Height = 1000, DataContext = vm };
        window.Show();
        await vm.StartAsync([]);
        Assert.Equal(6, vm.FoundTypes.Count);
        Assert.Equal("Galaxy? (0x80000000)", vm.FoundTypes.Last().Title);
        vm.AddArchetypeCommand.Execute(null);
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => (string?)t.Header == "Lists");
        Dispatcher.UIThread.RunJobs();

        // Omega's banlists can be scanned and copied into the pack to change.
        await vm.ScanOmegaBanlistsCommand.ExecuteAsync(null);
        var tcg = Assert.Single(vm.ScannedBanlists);
        Assert.Equal("TCG", tcg.Title);
        Assert.Equal("Pot of Greed", tcg.List.Entries.Single().Name);
        vm.CopyBanlistCommand.Execute(tcg);
        Assert.Equal("TCG (copy)", vm.SelectedBanlist!.Name);
        Assert.Equal(PackBanlist.FirstId, vm.SelectedBanlist.Model.Id);

        // Cards are found by name and put on it.
        vm.BanlistAddLimit = vm.BanlistLimitOptions.First(o => o.Value == Limitation.Limited);
        vm.BanlistSearch = "Neos";
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();
        var neos = Assert.Single(vm.BanlistCandidates);
        vm.AddToBanlistCommand.Execute(neos);
        Assert.Equal(2, vm.SelectedBanlist.Entries.Count);
        Assert.Equal("1 Forbidden  ·  1 Limited", vm.SelectedBanlist.Summary);
        Assert.True(vm.IsDirty);

        Dispatcher.UIThread.RunJobs();
        var dir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, "10-lists-tab.png"));
    }
}
