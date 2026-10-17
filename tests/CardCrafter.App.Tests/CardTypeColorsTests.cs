using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using CardCrafter.App.Services;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.Tests;

public class CardTypeColorsTests
{
    private static CardData Monster(MonsterFrame frame, bool pendulum = false) => new()
    {
        Name = $"{frame} Monster", Kind = CardKind.Monster, Frame = frame, Level = 4,
        Abilities = pendulum ? MonsterAbility.Pendulum : MonsterAbility.None,
    };

    private static IEnumerable<CardData> Samples() =>
    [
        Monster(MonsterFrame.Normal), Monster(MonsterFrame.Effect), Monster(MonsterFrame.Ritual), Monster(MonsterFrame.Fusion),
        Monster(MonsterFrame.Synchro), Monster(MonsterFrame.Xyz), Monster(MonsterFrame.Link), Monster(MonsterFrame.Effect, pendulum: true),
        new() { Name = "A Spell", Kind = CardKind.Spell, SpellKind = SpellKind.QuickPlay },
        new() { Name = "A Trap", Kind = CardKind.Trap, TrapKind = TrapKind.Counter },
    ];

    [AvaloniaFact]
    public void EachKindOfCard_HasItsOwnFrameColour()
    {
        var colours = Samples().Where(c => !c.IsPendulum).Select(CardTypeColors.Frame).ToList();
        Assert.Equal(colours.Count, colours.Distinct().Count());
        Assert.IsType<LinearGradientBrush>(CardTypeColors.Wash(Monster(MonsterFrame.Effect, pendulum: true)));
        Assert.IsType<SolidColorBrush>(CardTypeColors.Wash(new CardData { Kind = CardKind.Trap }));
    }

    [AvaloniaFact]
    public void CardLists_AreTintedByCardType()
    {
        var dir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "cardcrafter-shots");
        Directory.CreateDirectory(dir);
        var rows = Samples().Select((c, i) => { c.Id = 90000000 + i; return new LibraryCard(CdbEncoder.Encode(c, [], 4), "test", "test"); }).ToList();

        var vm = new MainViewModel(new FakeDialogs(), new AppSettings());
        foreach (var row in rows) vm.AddCardFromLibrary(row, null);
        var window = new MainWindow { Width = 1360, Height = 900, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.All(vm.Cards, c => Assert.NotNull(c.TypeWash));
        window.UpdateLayout();
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, "card-list-colours.png"));

        var browser = new CardBrowserWindow { Width = 900, Height = 700 };
        browser.Show();
        var bvm = new CardBrowserViewModel(vm);
        foreach (var row in rows) bvm.Results.Add(row);
        browser.DataContext = bvm;
        Dispatcher.UIThread.RunJobs();
        browser.UpdateLayout();
        browser.CaptureRenderedFrame()?.Save(Path.Combine(dir, "browser-colours.png"));
    }
}
