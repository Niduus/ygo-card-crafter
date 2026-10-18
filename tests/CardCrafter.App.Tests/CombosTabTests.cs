using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Combos;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.Tests;

public class CombosTabTests
{
    private const long Ash = 14558127, SnakeAsh = 9674034, Oak = 45663742, Utopia = 84013237, Pot = 55144522;

    private const string DeckList = """
        Monster
        2 Ash Blossom & Joyous Spring
        1 Snake-Eye Ash
        2 Snake-Eye Oak
        Spell
        3 Pot of Greed
        Extra
        Number 39: Utopia
        Side
        Unknown Card Name
        """;

    private static CardProject Card(long id, string name, CardKind kind, MonsterFrame frame, string text) => new()
    {
        Card = new CardData { Id = id, Name = name, Kind = kind, Frame = frame, Level = 4, Atk = 1800, Def = 1000, Text = text, Attribute = CardAttribute.Fire, Race = CardRace.Pyro },
    };

    /// <summary>A pack holding the deck's cards (the tests have no YGO Omega to look them up in), opened in the editor.</summary>
    private static (MainViewModel Main, TempFile File) Open(FakeDialogs? dialogs = null)
    {
        var pack = new CardPack { Name = "Combo test" };
        pack.Cards.Add(Card(Ash, "Ash Blossom & Joyous Spring", CardKind.Monster, MonsterFrame.Effect,
            "When a card or effect is activated that includes any of these effects (Quick Effect): You can discard this card; negate that effect.\n● Add a card from the Deck to the hand.\nYou can only use this effect of \"Ash Blossom & Joyous Spring\" once per turn."));
        pack.Cards.Add(Card(SnakeAsh, "Snake-Eye Ash", CardKind.Monster, MonsterFrame.Effect,
            "If this card is Normal or Special Summoned: You can add 1 Level 1 FIRE monster from your Deck to your hand. You can send 2 face-up cards you control to the GY, including this card; Special Summon 1 \"Snake-Eye\" monster from your hand or Deck, except \"Snake-Eye Ash\". You can only use each effect of \"Snake-Eye Ash\" once per turn."));
        pack.Cards.Add(Card(Oak, "Snake-Eye Oak", CardKind.Monster, MonsterFrame.Effect,
            "If this card is Normal or Special Summoned: You can Special Summon 1 Level 1 FIRE monster from your hand or GY."));
        pack.Cards.Add(Card(Utopia, "Number 39: Utopia", CardKind.Monster, MonsterFrame.Xyz,
            "2 Level 4 monsters\nWhen a monster declares an attack: You can detach 1 material from this card; negate the attack. If this card is targeted for an attack, while it has no material: Destroy this card."));
        pack.Cards.Add(Card(Pot, "Pot of Greed", CardKind.Spell, MonsterFrame.Effect, "Draw 2 cards."));
        var file = new TempFile(".ycc");
        PackSerializer.Save(pack, file.Path);
        var main = new MainViewModel(dialogs ?? new FakeDialogs(), new AppSettings());
        Assert.True(main.OpenPath(file.Path));
        return (main, file);
    }

    private static Option<ComboStepKind> Kind(CombosViewModel c, ComboStepKind kind) => c.StepKinds.First(k => k.Value == kind);

    /// <summary>Builds the combo the way the user would: deck from a pasted list, a hand from it, then the steps in the editor.</summary>
    private static async Task<CombosViewModel> WriteCombo(MainViewModel main)
    {
        var c = main.Combos;
        Assert.True(c.IsEmpty);
        c.NewComboCommand.Execute(null);
        Assert.True(c.IsDeckMode);
        c.ComboName = "Snake-Eye into Utopia";
        c.ComboNotes = "A two-card line to a Rank 4.";
        c.PasteText = DeckList;
        await c.ImportPastedCommand.ExecuteAsync(null);
        Assert.Equal(8, c.Combo!.Deck.Main.Count);
        Assert.Equal([Utopia], c.Combo.Deck.Extra);
        Assert.Contains("Unknown Card Name", c.ImportReport);
        foreach (var code in new[] { SnakeAsh, Oak, Ash, Pot })
            c.ToHandCommand.Execute(c.MainDeck.First(e => e.Code == code));
        Assert.Equal(4, c.HandChips.Count);

        c.AddStepCommand.Execute(Kind(c, ComboStepKind.NormalSummon));
        Assert.True(c.IsStepsMode);
        var e = c.Editor!;
        Assert.All(e.CardChoices, choice => Assert.Equal(ComboZone.Hand, choice.Zone));   // a Normal Summon picks from the hand
        e.Card = e.CardChoices.First(x => x.Code == SnakeAsh);
        c.Editor!.Note = "Ash searches a Level 1 FIRE when it is Summoned.";

        c.AddStepCommand.Execute(Kind(c, ComboStepKind.ActivateEffect));
        e = c.Editor!;
        e.Card = e.CardChoices.First(x => x.Code == SnakeAsh);
        e = c.Editor!;
        Assert.Equal(ComboZone.MonsterZone, e.Card!.Zone);
        e.Effect = e.EffectChoices.First(x => x.Value == 1);
        e.AddMaterialCommand.Execute(null);
        var row = c.Editor!.Materials.Single();
        row.Card = row.CardChoices.First(x => x.Code == Oak && x.Zone == ComboZone.Deck);
        c.Editor!.Materials.Single().To = c.Editor!.Materials.Single().ToChoices.First(z => z.Value == ComboZone.Hand);
        c.Editor!.Note = "Search Oak (any Level 1 FIRE works).";

        c.AddStepCommand.Execute(Kind(c, ComboStepKind.SpecialSummon));
        e = c.Editor!;
        e.Card = e.CardChoices.First(x => x.Code == Oak && x.Zone == ComboZone.Hand);
        c.Editor!.Position = c.Editor!.PositionChoices.First(p => p.Value == ComboPosition.Defense);

        c.AddStepCommand.Execute(Kind(c, ComboStepKind.XyzSummon));
        e = c.Editor!;
        e.Card = e.CardChoices.Single(x => x.Code == Utopia);
        c.Editor!.AddMaterialCommand.Execute(null);
        c.Editor!.AddMaterialCommand.Execute(null);
        c.Editor!.Note = "Two Level 4s make Utopia; both go under it.";

        c.AddStepCommand.Execute(Kind(c, ComboStepKind.ActivateCard));
        e = c.Editor!;
        e.Card = e.CardChoices.First(x => x.Code == Pot && x.Zone == ComboZone.Hand);
        c.Editor!.AddMaterialCommand.Execute(null);
        row = c.Editor!.Materials.Single();
        row.Card = row.CardChoices.First(x => x.Code == Pot && x.Zone == ComboZone.Deck);
        c.Editor!.Materials.Single().To = c.Editor!.Materials.Single().ToChoices.First(z => z.Value == ComboZone.Hand);

        c.AddStepCommand.Execute(Kind(c, ComboStepKind.Interruption));
        e = c.Editor!;
        e.CardSearch = "Ash Blossom";
        e = c.Editor!;
        e.Card = e.CardChoices.First(x => x.Code == Ash);
        c.Editor!.Note = "Ash Blossom on step 2 stops the search: without Oak there is no second Level 4, so keep another one in hand.";
        return c;
    }

    [AvaloniaFact]
    public async Task TheMaker_RecordsSteps_AndTheBoardFollows()
    {
        var (main, file) = Open();
        using var _ = file;
        var c = await WriteCombo(main);
        Assert.Equal(6, c.StepRows.Count);
        Assert.All(c.StepRows, r => Assert.False(r.IsFlagged, r.Problems));
        Assert.Equal("2. Activate the effect of Snake-Eye Ash (effect 1): add Snake-Eye Oak from the Deck to the hand",
            $"{c.StepRows[1].Number}. {c.StepRows[1].Text}");

        c.SelectedStepIndex = 3;
        var board = c.StepsBoard!;
        var utopia = board.MonsterZones.Single(t => t.HasCard);
        Assert.Equal("Number 39: Utopia", utopia.Name);
        Assert.Equal(2, utopia.MaterialCount);
        Assert.True(utopia.Highlight);
        Assert.Contains("Snake-Eye Oak", utopia.MaterialNames);
        Assert.Equal(0, board.ExtraDeck.Count);
        Assert.Equal("The board after step 4", c.StepsBoardLabel);

        // Deleting the Normal Summon leaves Ash in the hand: its effect and the Xyz Summon can't be played any more.
        c.SelectedStepIndex = 0;
        c.DeleteStepCommand.Execute(null);
        Assert.Equal(5, c.StepRows.Count);
        Assert.True(c.StepRows[0].IsFlagged);
        Assert.Contains("Snake-Eye Ash isn't in a Monster Zone at this point: it is in the hand", c.StepRows[0].Problems);
        Assert.True(c.StepRows[2].IsFlagged);
        Assert.Contains("can't be played as written", c.StepsSummary);

        c.UndoCommand.Execute(null);
        Assert.Equal(6, c.StepRows.Count);
        Assert.All(c.StepRows, r => Assert.False(r.IsFlagged));

        // Moving a step changes the order the replay follows.
        c.SelectedStepIndex = 2;
        c.MoveStepUpCommand.Execute(null);
        Assert.Equal(1, c.SelectedStepIndex);
        Assert.StartsWith("Special Summon Snake-Eye Oak", c.StepRows[1].Text);
        Assert.StartsWith("Activate the effect of Snake-Eye Ash", c.StepRows[2].Text);
        c.UndoCommand.Execute(null);
        Assert.StartsWith("Special Summon Snake-Eye Oak", c.StepRows[2].Text);

        // Changing a step's kind keeps the editor working.
        c.SelectedStepIndex = 5;
        c.Editor!.Kind = Kind(c, ComboStepKind.Note);
        Assert.False(c.Editor!.NeedsCard);
        c.UndoCommand.Execute(null);
        Assert.Equal(ComboStepKind.Interruption, c.Combo!.Steps[5].Kind);
        Assert.True(main.IsDirty);

        Assert.Contains("1. Normal Summon Snake-Eye Ash", c.ComboAsText);
        Assert.Contains("   Ash searches a Level 1 FIRE", c.ComboAsText);
    }

    [AvaloniaFact]
    public async Task TheViewer_PlaysTheStepsBack_WithTheUsedEffect()
    {
        var (main, file) = Open();
        using var _ = file;
        var c = await WriteCombo(main);
        c.IsPlayMode = true;
        Assert.Equal(0, c.PlayPosition);
        Assert.StartsWith("Opening hand: Snake-Eye Ash, Snake-Eye Oak, Ash Blossom", c.PlayTitle);
        Assert.Equal("A two-card line to a Rank 4.", c.PlayNote);
        Assert.False(c.PlayPreviousCommand.CanExecute(null));

        c.PlayNextCommand.Execute(null);
        c.PlayNextCommand.Execute(null);
        Assert.Equal("Step 2 of 6", c.PlayHeader);
        Assert.Equal(SnakeAsh, c.PlayCard!.Id);
        var used = Assert.Single(c.PlayEffects, l => l.IsUsed);
        Assert.StartsWith("If this card is Normal or Special Summoned", used.Text);
        Assert.Contains(c.PlayEffects, l => l.IsRule && l.Text.StartsWith("You can only use each effect"));
        Assert.Equal(["Snake-Eye Oak: Deck → Hand"], c.PlayMoves);
        Assert.Contains(c.PlayBoard!.Hand, t => t.Name == "Snake-Eye Oak" && t.Highlight);

        c.PlayLastCommand.Execute(null);
        Assert.False(c.PlayNextCommand.CanExecute(null));
        c.ShowEndBoard = true;
        var field = c.EndGroups.First();
        Assert.Equal("On the field (1)", field.Title);
        Assert.Contains("materials: Snake-Eye Ash, Snake-Eye Oak", field.Cards.Single().Detail);
        Assert.Contains("2 Level 4 monsters", field.Cards.Single().Text);
        Assert.Contains(c.EndGroups, g => g.Title == "In the GY (1)");
        c.PlaySlider = 1;
        Assert.Equal(1, c.PlayPosition);
    }

    [AvaloniaFact]
    public async Task Combos_AreSavedInThePack_SharedAsFiles_AndUndoneWithTheAssistant()
    {
        var dialogs = new FakeDialogs();
        var (main, file) = Open(dialogs);
        using var _ = file;
        var c = await WriteCombo(main);

        Assert.True(await main.SavePack());
        main.OpenPath(file.Path);
        Assert.Equal("Snake-Eye into Utopia", main.Combos.Combo!.Name);
        Assert.Equal(6, main.Combos.StepRows.Count);

        using var shared = new TempFile(ComboFile.Extension);
        dialogs.NextFile = shared.Path;
        await main.Combos.ExportComboFileCommand.ExecuteAsync(null);
        Assert.Contains("\"Snake-Eye Ash\"", File.ReadAllText(shared.Path));
        await main.Combos.ImportComboFileCommand.ExecuteAsync(null);
        Assert.Equal(2, main.Pack.Combos.Count);
        Assert.True(main.Combos.IsPlayMode);
        main.Combos.UndoCommand.Execute(null);
        Assert.Single(main.Pack.Combos);

        // The assistant's changes to combos are undone with its other changes.
        var before = main.SnapshotCards();
        var tools = new Core.Assistant.AssistantTools(main);
        var made = tools.Run("create_combo", System.Text.Json.JsonDocument.Parse("""{"name":"From the assistant","copy_deck_from":0,"opening_hand":["Pot of Greed"]}""").RootElement);
        Assert.False(made.IsError, made.Content);
        Assert.Equal(2, main.Combos.Items.Count);
        Assert.True(main.RestoreCards(before));
        Assert.Single(main.Combos.Items);
    }

    [AvaloniaFact]
    public async Task Screenshots_OfTheMakerAndTheViewer()
    {
        var (main, file) = Open();
        using var _ = file;
        var window = new MainWindow { Width = 1100, Height = 800 };
        window.DataContext = main;
        window.Show();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => (string?)t.Header == "Combos");
        Dispatcher.UIThread.RunJobs();
        var dir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "cardcrafter-shots");
        Directory.CreateDirectory(dir);
        void Shot(string name)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name));
        }
        Shot("combos-empty.png");

        var c = await WriteCombo(main);
        c.IsDeckMode = true;
        Shot("combos-deck.png");
        c.IsStepsMode = true;
        c.SelectedStepIndex = 1;
        Shot("combos-maker.png");
        c.SelectedStepIndex = 3;
        Shot("combos-maker-xyz.png");
        // A tall window shows the whole step editor under the board.
        window.Height = 1300;
        c.SelectedStepIndex = 1;
        Shot("combos-step-editor.png");
        window.Height = 800;

        c.PlayPosition = 2;
        c.IsPlayMode = true;
        Shot("combos-viewer.png");
        c.ShowEndBoard = true;
        Shot("combos-end-board.png");
        Assert.True(File.Exists(Path.Combine(dir, "combos-viewer.png")) || Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") is null);
    }
}

/// <summary>A file path in the temp folder, deleted afterwards.</summary>
public sealed class TempFile(string extension) : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cc-{Guid.NewGuid():N}{extension}");
    public void Dispose()
    {
        try { File.Delete(Path); } catch (IOException) { }
    }
}
