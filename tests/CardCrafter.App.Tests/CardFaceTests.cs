using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.Tests;

public class CardFaceTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "cardcrafter-shots");

    private static CardViewModel Vm(CardData data, CardPack? pack = null, Func<OmegaCatalog>? catalog = null)
    {
        pack ??= new CardPack();
        var project = new CardProject { Card = data };
        pack.Cards.Add(project);
        return new CardViewModel(project, () => pack, () => { }, catalog);
    }

    [AvaloniaFact]
    public void FacesRenderForEveryKindOfCard() => RenderAllKinds("7-card-faces.png");

    private static void RenderAllKinds(string shot)
    {
        var art = new Bitmap(Path.Combine(AppContext.BaseDirectory, "sample-art.png"));
        var cards = new[]
        {
            new CardData { Name = "Dragon Commander", Attribute = CardAttribute.Light, Race = CardRace.Dragon, Level = 4, Atk = 1700, Def = 1200,
                Text = "If this card is Normal or Special Summoned: You can add 1 Level 4 or lower Dragon monster from your Deck to your hand. You can only use this effect of \"Dragon Commander\" once per turn." },
            new CardData { Name = "Starfall Synchro Dragon of the Endless Night Sky", Frame = MonsterFrame.Synchro, Attribute = CardAttribute.Dark, Race = CardRace.Dragon, Level = 8, Atk = 3000, Def = 2500, Text = "1 Tuner + 1+ non-Tuner monsters" },
            new CardData { Name = "Rank Four Knight", Frame = MonsterFrame.Xyz, Attribute = CardAttribute.Earth, Race = CardRace.Warrior, Level = 4, Atk = 2500, Def = 2000, Text = "2 Level 4 monsters" },
            new CardData { Name = "Link Weaver", Frame = MonsterFrame.Link, Attribute = CardAttribute.Water, Race = CardRace.Cyberse, Atk = 2300,
                LinkMarkers = LinkMarker.BottomLeft | LinkMarker.BottomRight | LinkMarker.Top, Text = "2+ Effect Monsters" },
            new CardData { Name = "Pendulum Magician", Abilities = MonsterAbility.Pendulum, Attribute = CardAttribute.Dark, Race = CardRace.Spellcaster, Level = 4,
                LeftScale = 1, RightScale = 8, Atk = 1800, Def = 1000, PendulumText = "Once per turn: You can draw 1 card.", Text = "A magician who swings between worlds." },
            new CardData { Name = "Old Vanilla", Frame = MonsterFrame.Normal, Attribute = CardAttribute.Fire, Race = CardRace.Pyro, Level = 3, Atk = 1200, Def = -2, Text = "A flame that never goes out." },
            new CardData { Name = "Jar of Avarice", Kind = CardKind.Spell, SpellKind = SpellKind.QuickPlay, Text = "Draw 2 cards." },
            new CardData { Name = "Hole in the Floor", Kind = CardKind.Trap, TrapKind = TrapKind.Continuous, Text = "When your opponent Normal or Special Summons a monster: banish 1 monster your opponent controls." },
        };
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (card, i) in cards.Select((c, i) => (c, i)))
        {
            var vm = Vm(card);
            var face = new CardFace { Width = 300, Margin = new Avalonia.Thickness(8) };
            face.Update(vm.Data, vm.TypeLine, vm.FaceText, i % 2 == 0 ? art : null);
            panel.Children.Add(face);
        }
        var window = new Window { Width = 1300, Height = 920, Content = panel, Background = Avalonia.Media.Brushes.Black };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, shot));
    }

    /// <summary>
    /// Renders every card kind over the OmegaCustom textures. Runs only when CARDCRAFTER_OMEGACUSTOM points to a
    /// checkout's Textures/Interface folder; the textures themselves are never committed here.
    /// </summary>
    [AvaloniaFact]
    public void FacesRenderOverOmegaCustomTextures()
    {
        var folder = Environment.GetEnvironmentVariable("CARDCRAFTER_OMEGACUSTOM");
        if (folder is null || !Directory.Exists(folder)) return;
        Assert.True(CardCrafter.App.Services.FrameImages.Load(folder) >= 20);
        try
        {
            RenderAllKinds("7b-card-faces-omegacustom.png");
        }
        finally
        {
            CardCrafter.App.Services.FrameImages.Load(null);
        }
    }

    [AvaloniaFact]
    public void GenerateAvoidsOmegaAndPackPasscodes_AndClashesWarn()
    {
        using var tmp = new TempDirectory();
        var db = Path.Combine(tmp.Path, "db");
        CdbFile.WriteFresh(db, [CdbEncoder.Encode(new CardData { Id = 100000001, Name = "Official Card" }, [], 3)]);
        var catalog = OmegaCatalog.Load([db], tmp.Path);
        var vm = Vm(new CardData { Id = 100000001, Name = "Mine" }, catalog: () => catalog);
        Assert.Contains("already used in Omega by Official Card", vm.PasscodeWarning);
        Assert.Contains(vm.Diagnostics, d => d.Text.Contains("already used in Omega"));

        vm.GeneratePasscodeCommand.Execute(null);
        Assert.NotEqual(100000001, (long)vm.Id!);
        Assert.Equal(0, (long)vm.Id! % 10_000);
        Assert.False(vm.HasPasscodeWarning);
    }

    [AvaloniaFact]
    public void DuplicatePasscodeInPack_IsAnError()
    {
        var pack = new CardPack();
        pack.Cards.Add(new CardProject { Card = new CardData { Id = 100000001, Name = "First" } });
        var vm = Vm(new CardData { Id = 100000001, Name = "Second" }, pack);
        Assert.True(vm.HasErrors);
        Assert.Contains("also used by \"First\"", vm.PasscodeWarning);
    }

    [AvaloniaFact]
    public void CustomTypesAppearInTheTypeList()
    {
        var pack = new CardPack { CustomTypes = [new CustomType { Value = 0x8000000, Name = "Cyborg" }] };
        var vm = Vm(new CardData(), pack);
        var cyborg = vm.RaceOptions.Single(o => o.Label.StartsWith("Cyborg"));
        vm.Race = cyborg;
        Assert.Equal((CardRace)0x8000000, vm.Data.Race);
        Assert.Equal("[Cyborg/Effect]", vm.TypeLine);
    }

    [AvaloniaFact]
    public void PngArtIsExportedAsJpeg()
    {
        using var tmp = new TempDirectory();
        Core.Omega.PackExporter.ConvertToJpeg = (s, d) => typeof(App).Assembly.GetType("CardCrafter.App.ImageTools")!
            .GetMethod("ConvertToJpeg", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .Invoke(null, [s, d]) is true;
        var pack = new CardPack { Name = "Art" };
        pack.Cards.Add(new CardProject { Card = new CardData { Id = 100010000, Name = "Art Card" }, ArtworkPath = Path.Combine(AppContext.BaseDirectory, "sample-art.png") });
        var result = Core.Omega.PackExporter.Export(pack, tmp.Path);
        var jpg = Path.Combine(tmp.Path, "pics", "100010000.jpg");
        Assert.Equal(jpg, result.Cards[0].ImagePath);
        var head = File.ReadAllBytes(jpg).Take(2).ToArray();
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, head);
    }

    [AvaloniaFact]
    public void MainWindow_NewCardsGetGeneratedPasscodes()
    {
        var vm = new MainViewModel(new FakeDialogs(), new AppSettings());
        vm.AddCardCommand.Execute("Monster");
        var id = vm.SelectedCard!.Project.Card.Id;
        Assert.Equal(0, id % 10_000);
        Assert.InRange(id, 100_000_000, 999_990_000);
    }
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("cc-app-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class OmegaCustomDownloadTests
{
    /// <summary>Talks to GitHub, so it only runs when CARDCRAFTER_NETWORK_TESTS is set.</summary>
    [AvaloniaFact]
    public async Task DownloadsAndLoadsOmegaCustomTextures()
    {
        if (Environment.GetEnvironmentVariable("CARDCRAFTER_NETWORK_TESTS") is null) return;
        using var tmp = new TempDirectory();
        using var http = CardCrafter.Core.Updates.UpdateChecker.CreateClient();
        var saved = await CardCrafter.App.Services.FrameImages.DownloadOmegaCustomAsync(http, tmp.Path);
        Assert.True(saved >= 25, $"saved {saved}");
        try
        {
            Assert.True(CardCrafter.App.Services.FrameImages.Load(tmp.Path) >= 25);
            Assert.NotNull(CardCrafter.App.Services.FrameImages.Geometry("effect")?.Art);
            Assert.NotNull(CardCrafter.App.Services.FrameImages.Geometry("spell")?.Text);
        }
        finally
        {
            CardCrafter.App.Services.FrameImages.Load(null);
        }
    }
}
