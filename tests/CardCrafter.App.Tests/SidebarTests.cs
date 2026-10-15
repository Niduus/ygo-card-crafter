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

public class SidebarTests
{
    [AvaloniaFact]
    public async Task ExpandedSidebarFitsItsWidth()
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
        CdbFile.WriteFresh(Path.Combine(files, "Databases", "Rush.db"), rows);

        var vm = new MainViewModel(new FakeDialogs(), new AppSettings { OmegaPath = omega });
        var window = new MainWindow { Width = 1360, Height = 1000, DataContext = vm };
        window.Show();
        await vm.StartAsync([]);
        Assert.Equal(6, vm.FoundTypes.Count);
        Assert.Equal("Galaxy? (0x80000000)", vm.FoundTypes.Last().Title);
        vm.AddArchetypeCommand.Execute(null);
        foreach (var e in window.GetVisualDescendants().OfType<Expander>()) e.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        var sidebarWidth = 280.0;
        foreach (var b in window.GetVisualDescendants().OfType<Button>().Where(b => b.FindAncestorOfType<Expander>() is not null && b.IsVisible))
            Assert.True(b.Bounds.Width <= sidebarWidth, $"{b.Content} is {b.Bounds.Width}px wide");

        var dir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, "10-sidebar.png"));
    }
}
