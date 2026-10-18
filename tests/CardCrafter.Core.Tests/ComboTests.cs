using System.Text.Json;
using CardCrafter.Core.Assistant;
using CardCrafter.Core.Combos;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

public class ComboTests
{
    public const long Ash = 14558127, SnakeAsh = 9674034, Oak = 45663742, Utopia = 84013237, Spider = 98978921, Pot = 55144522,
        Field = 22702055, Scale = 16178681, Lamp = 1000001, Token = 1000002;

    private static CardData Monster(long id, string name, MonsterFrame frame = MonsterFrame.Effect, MonsterAbility abilities = 0) =>
        new() { Id = id, Name = name, Kind = CardKind.Monster, Frame = frame, Abilities = abilities, Level = 4 };

    public static ComboCatalog Catalog() => new(
    [
        Card(Monster(Ash, "Ash Blossom & Joyous Spring"), "When a card or effect is activated that includes any of these effects (Quick Effect): You can discard this card; negate that effect.\n● Add a card from the Deck to the hand.\nYou can only use this effect of \"Ash Blossom & Joyous Spring\" once per turn."),
        Card(Monster(SnakeAsh, "Snake-Eye Ash"), "If this card is Normal or Special Summoned: You can add 1 Level 1 FIRE monster from your Deck to your hand. You can send 2 face-up cards you control to the GY, including this card; Special Summon 1 \"Snake-Eye\" monster from your hand or Deck, except \"Snake-Eye Ash\". You can only use each effect of \"Snake-Eye Ash\" once per turn."),
        Card(Monster(Oak, "Snake-Eye Oak"), "If this card is Normal or Special Summoned: You can Special Summon 1 Level 1 FIRE monster from your hand or GY."),
        Card(Monster(Utopia, "Number 39: Utopia", MonsterFrame.Xyz), "2 Level 4 monsters\nWhen a monster declares an attack: You can detach 1 material from this card; negate the attack."),
        Card(Monster(Spider, "Link Spider", MonsterFrame.Link), "1 Normal Monster\nOnce per turn: You can Special Summon 1 Level 4 or lower Normal Monster from your hand to your zone this card points to."),
        Card(new CardData { Id = Pot, Name = "Pot of Greed", Kind = CardKind.Spell }, "Draw 2 cards."),
        Card(new CardData { Id = Field, Name = "Mystic Mine", Kind = CardKind.Spell, SpellKind = SpellKind.Field }, "Nothing can attack."),
        Card(Monster(Scale, "Odd-Eyes Pendulum Dragon", abilities: MonsterAbility.Pendulum), "Pendulum Scale = 4\n[ Pendulum Effect ]\nYou can reduce the battle damage you take to 0.\n----\n[ Monster Effect ]\nIf this card battles, the damage is doubled."),
        Card(new CardData { Id = Lamp, Name = "Lamp of Ages", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous }, "Once per turn: You can draw 1 card."),
        Card(Monster(Token, "Lamp Token", MonsterFrame.Token), ""),
    ]);

    private static ComboCard Card(CardData d, string text) => new(d.Id, d.Name, d, text);

    private static Combo NewCombo(params long[] hand) => new()
    {
        Name = "Test line",
        Deck = new ComboDeck { Main = [Ash, Ash, Ash, SnakeAsh, Oak, Oak, Pot, Field, Scale, Lamp], Extra = [Utopia, Spider] },
        StartingHand = [.. hand],
    };

    private static ComboStep Step(ComboStepKind kind, long code, params ComboMove[] materials) => new() { Kind = kind, Code = code, Materials = [.. materials] };
    private static ComboMove M(long code, ComboZone to = ComboZone.Auto, ComboZone from = ComboZone.Auto) => new() { Code = code, From = from, To = to };

    [Fact]
    public void Replay_MovesCards_AndKeepsTheBoardAfterEachStep()
    {
        var combo = NewCombo(SnakeAsh, Ash, Pot);
        combo.Steps.Add(Step(ComboStepKind.NormalSummon, SnakeAsh));
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.ActivateEffect, Code = SnakeAsh, Effect = 1, Materials = [M(Oak, ComboZone.Hand)] });
        combo.Steps.Add(Step(ComboStepKind.ActivateCard, Pot, M(Ash, ComboZone.Hand, ComboZone.Deck)));
        var replay = ComboReplay.Run(combo, Catalog());

        Assert.Empty(replay.StartProblems);
        Assert.Equal(0, replay.FlaggedCount);
        Assert.Equal(3, replay.Start.Hand.Count);
        Assert.Equal(SnakeAsh, replay.BoardAfter(1).MonsterZones[0]!.Code);
        Assert.Equal(ComboPosition.Attack, replay.BoardAfter(1).MonsterZones[0]!.Position);
        Assert.Contains(replay.BoardAfter(2).Hand, c => c.Code == Oak);
        Assert.Contains($"{Oak}#1", replay.Steps[1].Touched);
        Assert.Contains($"{SnakeAsh}#1", replay.Steps[1].Touched);
        // A Normal Spell ends in the GY once it resolves; the drawn Ash is the second copy (the first is in the hand).
        Assert.Equal(Pot, replay.End.Graveyard.Single().Code);
        Assert.Equal([1, 2], replay.End.Hand.Where(c => c.Code == Ash).Select(c => c.Copy).Order());
        Assert.Single(replay.BoardAfter(1).Hand, c => c.Code == Ash);
    }

    [Fact]
    public void Replay_XyzMaterialsGoUnderTheMonster_AndToTheGyWhenItLeaves()
    {
        var combo = NewCombo(SnakeAsh, Oak);
        combo.Steps.Add(Step(ComboStepKind.NormalSummon, SnakeAsh));
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.SpecialSummon, Code = Oak, From = ComboZone.Hand, Position = ComboPosition.Defense });
        combo.Steps.Add(Step(ComboStepKind.XyzSummon, Utopia, M(SnakeAsh), M(Oak)));
        combo.Steps.Add(Step(ComboStepKind.LinkSummon, Spider, M(Utopia)));
        var replay = ComboReplay.Run(combo, Catalog());

        Assert.Equal(0, replay.FlaggedCount);
        Assert.Equal(ComboPosition.Defense, replay.BoardAfter(2).MonsterZones[1]!.Position);
        var xyz = replay.BoardAfter(3);
        var utopia = xyz.MonsterZones.Single(m => m is not null)!;
        Assert.Equal(Utopia, utopia.Code);
        Assert.Equal([SnakeAsh, Oak], utopia.Materials.Select(m => m.Code));
        Assert.Empty(xyz.Graveyard);
        Assert.Contains(xyz.Spots(), s => s.Zone == ComboZone.XyzMaterial && s.Card.Code == Oak);

        var end = replay.End;
        Assert.Equal(Spider, end.ExtraMonsterZones[0]!.Code);   // a Link Monster from the Extra Deck goes to the Extra Monster Zone
        Assert.All(end.MonsterZones, m => Assert.Null(m));
        Assert.Equal([SnakeAsh, Oak, Utopia], end.Graveyard.Select(c => c.Code));
        Assert.Contains(replay.Steps[3].Moves, m => m.Code == Oak && m.From == ComboZone.XyzMaterial && m.To == ComboZone.Graveyard);
    }

    [Fact]
    public void Replay_FlagsStepsThatBecameImpossible_AfterAnEarlierStepIsDeleted()
    {
        var combo = NewCombo(SnakeAsh, Oak);
        combo.Steps.Add(Step(ComboStepKind.NormalSummon, SnakeAsh));
        combo.Steps.Add(Step(ComboStepKind.SpecialSummon, Oak));
        combo.Steps.Add(Step(ComboStepKind.XyzSummon, Utopia, M(SnakeAsh, from: ComboZone.MonsterZone), M(Oak, from: ComboZone.MonsterZone)));
        combo.Steps.Add(Step(ComboStepKind.SendToGraveyard, Ash, M(0)));
        Assert.Equal(1, ComboReplay.Run(combo, Catalog()).FlaggedCount);   // the last step names no card to move with it

        combo.Steps.RemoveAt(3);
        combo.Steps.RemoveAt(0);
        var replay = ComboReplay.Run(combo, Catalog());
        Assert.False(replay.Steps[0].IsFlagged);
        Assert.True(replay.Steps[1].IsFlagged);
        Assert.Contains("Snake-Eye Ash isn't in a Monster Zone at this point: it is in the hand.", replay.Steps[1].Problems);
        // What could be played still was: Oak went under Utopia, Ash stayed in the hand.
        Assert.Equal(Utopia, replay.End.MonsterZones[0]!.Code);
        Assert.Equal([Oak], replay.End.MonsterZones[0]!.Materials.Select(m => m.Code));
        Assert.Contains(replay.End.Hand, c => c.Code == SnakeAsh);

        // A card that isn't in the hand any more can't be Normal Summoned: the step is flagged and changes nothing.
        combo.Steps.Insert(0, Step(ComboStepKind.NormalSummon, Pot));
        replay = ComboReplay.Run(combo, Catalog());
        Assert.True(replay.Steps[0].IsFlagged);
        Assert.Contains(replay.Steps[0].Problems, p => p.Contains("Pot of Greed isn't in the hand"));
        Assert.Equal(replay.Start.Hand.Count, replay.BoardAfter(1).Hand.Count);
    }

    [Fact]
    public void Replay_FollowsTheRules_ForPendulums_FieldSpells_TokensAndFullZones()
    {
        var combo = NewCombo(Scale, Field, Lamp);
        combo.Steps.Add(Step(ComboStepKind.ActivateCard, Scale));
        combo.Steps.Add(Step(ComboStepKind.SendToGraveyard, Scale));
        combo.Steps.Add(Step(ComboStepKind.ActivateCard, Field));
        combo.Steps.Add(Step(ComboStepKind.Set, Lamp));
        combo.Steps.Add(Step(ComboStepKind.ActivateCard, Lamp));
        for (int i = 0; i < 6; i++) combo.Steps.Add(new ComboStep { Kind = ComboStepKind.SpecialSummon, Code = Token, From = ComboZone.Token });
        combo.Steps.Add(Step(ComboStepKind.Banish, Token));
        var replay = ComboReplay.Run(combo, Catalog());

        Assert.True(replay.BoardAfter(1).SpellTrapZones[0]!.InPendulumZone);
        Assert.Equal(ComboZone.PendulumZone, replay.BoardAfter(1).Spots().First(s => s.Card.Code == Scale).Zone);
        var ed = replay.BoardAfter(2).ExtraDeck.Single(c => c.Code == Scale);
        Assert.Equal(ComboPosition.FaceUp, ed.Position);
        Assert.Contains("face-up to the Extra Deck", replay.Steps[1].Moves.Single().Note);
        Assert.Equal(Field, replay.End.FieldZone!.Code);
        Assert.Equal(ComboPosition.FaceDown, replay.BoardAfter(4).SpellTrapZones[1]!.Position);
        Assert.Equal(ComboPosition.FaceUp, replay.BoardAfter(5).SpellTrapZones[1]!.Position);   // a Continuous Spell stays, face-up
        Assert.False(replay.Steps[9].IsFlagged);
        Assert.True(replay.Steps[10].IsFlagged);   // the sixth Token finds no free Main Monster Zone
        Assert.Contains("no free Main Monster Zone", replay.Steps[10].Problems.Single());
        Assert.Equal(4, replay.End.MonsterZones.Count(m => m is not null));   // a banished Token is gone
        Assert.Empty(replay.End.Banished);
    }

    [Fact]
    public void Replay_ReportsHandProblems_AndDrawsTheSameRandomHandForASeed()
    {
        var combo = NewCombo(Ash, Ash, Ash, Ash, Utopia);
        var replay = ComboReplay.Run(combo, Catalog());
        Assert.Equal(3, replay.Start.Hand.Count);
        Assert.Equal(2, replay.StartProblems.Count);
        Assert.Contains("more copies of Ash Blossom & Joyous Spring", replay.StartProblems[0]);
        Assert.Contains("Number 39: Utopia is in the opening hand but not in the Main Deck", replay.StartProblems[1]);

        combo.RandomHand = true;
        combo.Seed = 42;
        var a = ComboReplay.Run(combo, Catalog()).Start.Hand.Select(c => c.Key).ToList();
        var b = ComboReplay.Run(combo, Catalog()).Start.Hand.Select(c => c.Key).ToList();
        Assert.Equal(5, a.Count);
        Assert.Equal(a, b);
        Assert.Equal(5, ComboReplay.Run(combo, Catalog()).Start.Deck.Count);
    }

    [Fact]
    public void CardText_SplitsIntoNumberedEffects()
    {
        var snake = CardEffects.Split(Catalog().Find(SnakeAsh)!.Text);
        Assert.Equal([1, 2, 0], snake.Select(p => p.Number));
        Assert.StartsWith("You can send 2 face-up cards", snake[1].Text);
        Assert.StartsWith("You can only use each effect", snake[2].Text);

        var utopia = CardEffects.Split(Catalog().Find(Utopia)!.Text);
        Assert.Equal("2 Level 4 monsters", utopia[0].Text);
        Assert.False(utopia[0].IsEffect);
        Assert.Equal(1, utopia[1].Number);

        var ash = CardEffects.Split(Catalog().Find(Ash)!.Text);
        Assert.Equal(2, ash.Count);
        Assert.Contains("● Add a card", ash[0].Text);

        var odd = CardEffects.Split(Catalog().Find(Scale)!.Text);
        Assert.Equal([("Pendulum Effect", 1), ("Monster Effect", 2)], odd.Select(p => (p.Section, p.Number)));

        var sarc = CardEffects.Split("Banish 1 card from your Deck, face-up. During your 2nd Standby Phase after this card's activation, add that card to the hand.");
        Assert.Single(sarc);
        var diabell = CardEffects.Split("You can Special Summon this card (from your hand) by sending 1 card from your hand or field to the GY. You can only Special Summon \"Diabellstar the Black Witch\" once per turn this way. You can only use each of the following effects of \"Diabellstar the Black Witch\" once per turn. If this card is Normal or Special Summoned: You can Set 1 \"Sinful Spoils\" Spell/Trap from your Deck. During your opponent's turn, if this card is sent from its owner's hand or field to the GY: You can send 1 card from your hand or field to the GY, and if you do, Special Summon this card.");
        Assert.Equal([1, 0, 0, 2, 3], diabell.Select(p => p.Number));
        Assert.Equal("Draw 2 cards.", CardEffects.Effect("Draw 2 cards.", 1));
    }

    [Fact]
    public void Ydk_ReadsSections_AlternateArts_AndKeepsUnknownPasscodes()
    {
        using var tmp = new TempDir();
        var db = Path.Combine(tmp.Path, "cards.cdb");
        CdbFile.WriteFresh(db,
        [
            new CdbRow { Id = Ash, Type = 0x21, Name = "Ash Blossom & Joyous Spring", Desc = "Quick Effect." },
            new CdbRow { Id = Ash + 1, Alias = Ash, Type = 0x21, Name = "Ash Blossom & Joyous Spring", Desc = "Alternate art." },
            new CdbRow { Id = Utopia, Type = 0x800021, Name = "Number 39: Utopia", Desc = "2 Level 4 monsters" },
            new CdbRow { Id = Pot, Type = 0x2, Name = "Pot of Greed", Desc = "Draw 2 cards." },
        ]);
        var catalog = ComboCatalog.Build(new CardPack(), CardLibrary.Load([db]));
        var ydk = "#created by someone\r\n#main\r\n14558127\r\n14558128\r\n55144522\r\n99999999\r\n#extra\r\n84013237\r\n!side\r\n14558127\r\n";
        Assert.True(DeckImport.LooksLikeYdk(ydk));
        var result = DeckImport.Read(ydk, catalog);
        Assert.Equal([Ash, Ash, Pot, 99999999], result.Deck.Main);
        Assert.Equal([Utopia], result.Deck.Extra);
        Assert.Equal([Ash], result.Deck.Side);
        Assert.Equal(["99999999"], result.Unknown);
        Assert.Equal(DeckImport.FromYdk(DeckImport.ToYdk(result.Deck), catalog).Deck.Main, result.Deck.Main);
    }

    [Fact]
    public void PastedDeckLists_ReadCountsAndSections_AndListWhatIsntFound()
    {
        var text = """
            Monster (5)
            3 Ash Blossom & Joyous Spring
            Snake-Eye Ash x2
            Number 39: Utopia
            Spell: 2
            2x pot of greed
            1 Pot of Grede
            Trap

            Extra (1)
            1 Link Spider
            Side Deck
            - 1 Mystic Mine
            Not A Real Card
            """;
        var result = DeckImport.Read(text, Catalog());
        Assert.Equal([Ash, Ash, Ash, SnakeAsh, SnakeAsh, Pot, Pot], result.Deck.Main);
        Assert.Equal([Utopia, Spider], result.Deck.Extra);   // an Extra Deck monster listed under Monster still goes to the Extra Deck
        Assert.Equal([Field], result.Deck.Side);
        Assert.Equal(["1 Pot of Grede", "Not A Real Card"], result.Unknown);
        Assert.Contains("not found: 1 Pot of Grede, Not A Real Card", result.Summary);
    }

    [Fact]
    public void ThePacksCards_MakeADeck()
    {
        var pack = new CardPack();
        pack.Cards.Add(new CardProject { Card = Monster(100000010, "Pack Knight") });
        pack.Cards.Add(new CardProject { Card = Monster(100000011, "Pack Dragon", MonsterFrame.Synchro) });
        pack.Cards.Add(new CardProject { Card = Monster(100000012, "Pack Token", MonsterFrame.Token) });
        var result = DeckImport.FromPack(pack);
        Assert.Equal([100000010L], result.Deck.Main);
        Assert.Equal([100000011L], result.Deck.Extra);
        var catalog = ComboCatalog.Build(pack, null);
        Assert.Equal("Pack Knight", catalog.Name(100000010));
        Assert.Equal("Pack Knight", catalog.FindByName("pack  knight")!.Name);
    }

    [Fact]
    public void Packs_SaveWithoutCombos_AsBefore_AndKeepTheirCombos()
    {
        var plain = new CardPack { Name = "Old" };
        plain.Cards.Add(new CardProject { Card = Monster(100000010, "Pack Knight") });
        var json = PackSerializer.ToJson(plain);
        Assert.DoesNotContain("combos", json);
        Assert.Empty(PackSerializer.FromJson(json).Combos);

        var combo = NewCombo(SnakeAsh, Ash);
        combo.Notes = "Plays through one hand trap.";
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.NormalSummon, Code = SnakeAsh, Note = "Starter." });
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.XyzSummon, Code = Utopia, Slot = 3, Materials = [M(SnakeAsh), new ComboMove { Code = Oak, Copy = 2, From = ComboZone.Hand }] });
        plain.Combos.Add(combo);
        var saved = PackSerializer.ToJson(plain);
        Assert.Contains("\"combos\"", saved);
        Assert.Contains("\"xyzSummon\"", saved);
        var back = PackSerializer.FromJson(saved);
        Assert.Equal(saved, PackSerializer.ToJson(back));
        Assert.Equal(3, back.Combos[0].Steps[1].Slot);
        Assert.Equal(2, back.Combos[0].Steps[1].Materials[1].Copy);
        Assert.Equal(ComboZone.Hand, back.Combos[0].Steps[1].Materials[1].From);
    }

    [Fact]
    public void ComboFiles_RoundTrip_WithTheCardNames()
    {
        var combo = NewCombo(SnakeAsh);
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.NormalSummon, Code = SnakeAsh, Note = "Go." });
        var json = ComboFile.ToJson(combo, Catalog());
        Assert.Contains("\"formatVersion\": 1", json);
        var back = ComboFile.FromJson(json);
        Assert.Equal("Snake-Eye Ash", back.Names[SnakeAsh]);
        Assert.Equal("Number 39: Utopia", back.Names[Utopia]);
        Assert.Empty(combo.Names);   // the combo itself is left as it was
        // Without the cards installed, the names still read right.
        Assert.Contains("1. Normal Summon Snake-Eye Ash", ComboText.ToText(back));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(combo.Steps, PackSerializer.Options), System.Text.Json.JsonSerializer.Serialize(back.Steps, PackSerializer.Options));
        Assert.Throws<InvalidDataException>(() => ComboFile.FromJson("{\"formatVersion\":1}"));
        Assert.Throws<InvalidDataException>(() => ComboFile.FromJson("not json"));
    }

    [Fact]
    public void CopyAsText_NumbersTheSteps_WithNamesAndNotes()
    {
        var combo = NewCombo(SnakeAsh, Oak);
        combo.Name = "Snake-Eye 2-card";
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.NormalSummon, Code = SnakeAsh, Note = "Search Oak." });
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.ActivateEffect, Code = SnakeAsh, Effect = 1, Materials = [M(Oak, ComboZone.Hand, ComboZone.Deck)] });
        combo.Steps.Add(Step(ComboStepKind.SpecialSummon, Oak));
        combo.Steps.Add(Step(ComboStepKind.XyzSummon, Utopia, M(SnakeAsh), M(Oak)));
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.Interruption, Code = Ash, Note = "Ash Blossom negates step 2." });
        combo.Steps.Add(new ComboStep { Kind = ComboStepKind.EndTurn });
        var text = ComboText.ToText(combo, Catalog());
        Assert.Equal("""
            **Snake-Eye 2-card**

            Opening hand: Snake-Eye Ash, Snake-Eye Oak

            1. Normal Summon Snake-Eye Ash
               Search Oak.
            2. Activate the effect of Snake-Eye Ash (effect 1): add Snake-Eye Oak from the Deck to the hand
            3. Special Summon Snake-Eye Oak
            4. Xyz Summon Number 39: Utopia using Snake-Eye Ash and Snake-Eye Oak
            5. Opponent activates Ash Blossom & Joyous Spring
               Ash Blossom negates step 2.
            6. End the turn

            End board:
            - Field: Number 39: Utopia with 2 materials
            - Hand: Snake-Eye Oak

            """.ReplaceLineEndings("\n"), text);
    }

    /// <summary>A pack holding the test cards, so the assistant's catalog knows them.</summary>
    public static CardPack PackWithTheCards()
    {
        var pack = new CardPack();
        foreach (var c in Catalog().All)
        {
            var data = PackSerializer.Clone(c.Data);
            data.Text = c.Text;
            pack.Cards.Add(new CardProject { Card = data });
        }
        return pack;
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TheAssistant_WritesCombos_OnlyWithTheDecksCards()
    {
        var ws = new PackWorkspace(PackWithTheCards());
        var tools = new AssistantTools(ws);
        Assert.Contains("no combos", tools.Run("list_combos", J("{}")).Content);

        var refused = tools.Run("create_combo", J("""
            {"name":"Utopia line","deck_text":"Monster\n3 Snake-Eye Ash\n2 Snake-Eye Oak\nExtra\nNumber 39: Utopia",
             "opening_hand":["Snake-Eye Ash"],
             "steps":[{"kind":"normalSummon","card":"Snake-Eye Ash"},{"kind":"activateCard","card":"Pot of Greed"}]}
            """));
        Assert.True(refused.IsError);
        Assert.Contains("step 2: Pot of Greed isn't in the combo's deck", refused.Content);
        Assert.Empty(ws.Pack.Combos);

        var made = tools.Run("create_combo", J("""
            {"name":"Utopia line","deck_text":"Monster\n3 Snake-Eye Ash\n2 Snake-Eye Oak\nExtra\nNumber 39: Utopia",
             "opening_hand":["Snake-Eye Ash", 45663742],
             "steps":[{"kind":"normalSummon","card":"Snake-Eye Ash","note":"Starter."},
                      {"kind":"activate_effect","card":"Snake-Eye Ash","effect":1,"materials":[{"card":"Snake-Eye Oak","from":"deck","to":"hand"}]},
                      {"kind":"specialSummon","card":"Snake-Eye Oak","from":"hand","position":"def"},
                      {"kind":"xyzSummon","card":"Number 39: Utopia","materials":[{"card":"Snake-Eye Ash"},{"card":"Snake-Eye Oak"}]},
                      {"kind":"interruption","card":"Ash Blossom & Joyous Spring","note":"Ash on step 2."}]}
            """));
        Assert.False(made.IsError, made.Content);
        Assert.Equal("Added the combo \"Utopia line\"", made.Activity);
        var combo = Assert.Single(ws.Pack.Combos);
        Assert.Equal(5, combo.Steps.Count);
        Assert.Equal(ComboPosition.Defense, combo.Steps[2].Position);
        Assert.Equal("Snake-Eye Ash", combo.Names[SnakeAsh]);
        Assert.Equal(0, ComboReplay.Run(combo, ws.ComboCards).FlaggedCount);
        Assert.Contains("Number 39: Utopia with 2 materials", made.Content);
        Assert.Equal(1, ws.Changes);

        var read = JsonDocument.Parse(tools.Run("get_combo", J("""{"index":0}""")).Content).RootElement;
        Assert.Equal("If this card is Normal or Special Summoned: You can add 1 Level 1 FIRE monster from your Deck to your hand.",
            read.GetProperty("steps")[1].GetProperty("effectText").GetString());
        var ashText = read.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("passcode").GetInt64() == SnakeAsh);
        Assert.StartsWith("2. You can send 2 face-up cards", ashText.GetProperty("effects")[1].GetString());
        Assert.False(read.TryGetProperty("startProblems", out _));

        var bad = tools.Run("replace_combo", J("""{"index":0,"steps":[{"kind":"activateEffect","card":"Snake-Eye Ash","effect":7}]}"""));
        Assert.True(bad.IsError);
        Assert.Contains("has no effect 7", bad.Content);
        var replaced = tools.Run("replace_combo", J("""{"index":0,"name":"Shorter","steps":[{"kind":"xyzSummon","card":"Number 39: Utopia","materials":[{"card":"Snake-Eye Ash","from":"monsterZone"}]}]}"""));
        Assert.False(replaced.IsError, replaced.Content);
        Assert.Equal("Shorter", ws.Pack.Combos[0].Name);
        Assert.Equal(5, ws.Pack.Combos[0].Deck.Main.Count);   // the deck stays
        Assert.Contains("in a Monster Zone at this point: it is in the hand", replaced.Content);
        Assert.Contains("\"flaggedSteps\": 1", tools.Run("list_combos", J("{}")).Content);
    }
}
