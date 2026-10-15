using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Block combinations that read and play like real cards.</summary>
public class BlockCoverageTests
{
    private static BlockInstance Block(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    /// <summary>"When your opponent Normal or Special Summons a monster(s) during their turn (Quick Effect): You can Special Summon this card from your hand."</summary>
    private static CardProject HandQuickSummon(string timing = "summon") => new()
    {
        Card = new CardData { Id = 100003001, Name = "Ambusher", Frame = MonsterFrame.Effect, Level = 7, Atk = 2500, Def = 2000 },
        Effects =
        [
            new EffectEvent
            {
                Label = "Special Summon", Limit = OncePerTurn.Hard,
                When = Block("when.quick", ("timing", timing), ("zone", "hand")),
                If = [Block("if.turn", ("whose", "opponent"))],
                Do = [Block("do.special_summon_self")],
            },
        ],
    };

    [Fact]
    public void HandQuickEffect_ReadsLikeACard()
    {
        var p = HandQuickSummon();
        Assert.Equal(
            "When your opponent Normal or Special Summons a monster(s) during their turn (Quick Effect): You can Special Summon this card from your hand. " +
            "You can only use this effect of \"Ambusher\" once per turn.",
            EffectTextGenerator.Generate(p));
        Assert.DoesNotContain("\n", EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.Message)));
        Assert.Contains("SetRange(LOCATION_HAND)", r.Lua);
        Assert.Contains("EVENT_SPSUMMON_SUCCESS", r.Lua);
    }

    [EngineFact]
    public void HandQuickEffect_SummonsItself_WhenTheOpponentSummonsOnTheirTurn()
    {
        var p = HandQuickSummon();
        new EngineScenario(p)
            .Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Card(Fiend, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Summon(Fiend).Activate(p.Card.Id).Turns(2)
            .ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})")
            .AssertPasses();
    }

    [EngineFact]
    public void HandQuickEffect_DoesNothing_WhenYouSummon()
    {
        var p = HandQuickSummon();
        new EngineScenario(p)
            .Card(p.Card.Id, 0, "hand").Card(Fiend, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Summon(Fiend).Activate(p.Card.Id)
            .ExpectActivations(0)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{p.Card.Id})")
            .AssertPasses();
    }

    [Fact]
    public void SpecialSummonThisCard_FromTheField_IsAnError()
    {
        var p = HandQuickSummon();
        p.Effects[0].When!.Values["zone"] = "auto";
        Assert.Contains(ScriptCompiler.Compile(p).Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("in the hand"));
    }

    [Fact]
    public void GraveyardEffects_SayWhereThisCardIs()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100003002, Name = "Spirit", Frame = MonsterFrame.Effect, Level = 4 },
            Effects =
            [
                new EffectEvent { When = Block("when.ignition", ("zone", "graveyard")), Do = [Block("do.draw")] },
                new EffectEvent { When = Block("when.ignition", ("zone", "graveyard")), Cost = [Block("cost.this_card", ("how", "banish"))], Do = [Block("do.draw")] },
                new EffectEvent { When = Block("when.event", ("event", "opp_special"), ("zone", "graveyard")), Do = [Block("do.special_summon_self", ("position", "defense"))] },
            ],
        };
        var lines = p.Effects.Select(e => EffectTextGenerator.Describe(e, p.Card)).ToList();
        Assert.Equal("If this card is in your GY: You can draw 1 card.", lines[0]);
        Assert.Equal("You can banish this card from your GY; draw 1 card.", lines[1]);
        Assert.Equal("If your opponent Special Summons a monster(s): You can Special Summon this card from your GY in Defense Position.", lines[2]);
        Assert.False(ScriptCompiler.Compile(p).HasErrors);
    }

    /// <summary>Every timing of the Quick Effect and every event of the trigger compiles to a script the engine loads.</summary>
    [EngineFact]
    public void EveryTimingAndEvent_Loads()
    {
        var cards = new List<CardProject>();
        long id = 100003100;
        foreach (var t in BlockRegistry.Find("when.quick")!.Param("timing")!.Choices)
            cards.Add(new CardProject
            {
                Card = new CardData { Id = id++, Name = "Q " + t.Value, Frame = MonsterFrame.Effect, Level = 4 },
                Effects = [new EffectEvent { When = Block("when.quick", ("timing", t.Value), ("zone", "hand_or_graveyard")), Do = [Block("do.special_summon_self")] }],
            });
        foreach (var ev in BlockRegistry.Find("when.event")!.Param("event")!.Choices)
            cards.Add(new CardProject
            {
                Card = new CardData { Id = id++, Name = "E " + ev.Value, Frame = MonsterFrame.Effect, Level = 4 },
                Effects = [new EffectEvent { When = Block("when.event", ("event", ev.Value), ("zone", "hand")), Do = [Block("do.special_summon_self")] }],
            });
        foreach (var c in cards) Assert.False(ScriptCompiler.Compile(c).HasErrors, c.Card.Name);
        // A few dozen cards per duel: the test harness has a fixed-size hand.
        foreach (var batch in cards.Chunk(25))
        {
            var s = new EngineScenario([.. batch]);
            foreach (var c in batch) s.Card(c.Card.Id, 0, "hand");
            s.Card(Warrior, 0, "deck", 5).Card(Fiend, 1, "hand").Card(Warrior, 1, "deck", 5).Summon(Fiend).Turns(2).AssertPasses();
        }
    }
}

public class ZoneWordingTests
{
    [Theory]
    [InlineData(Zone.Field, "Send 3 cards your opponent controls to the GY")]
    [InlineData(Zone.MonsterZone, "Send 3 monsters your opponent controls to the GY")]
    [InlineData(Zone.SpellTrapZone, "Send 3 Spell/Traps your opponent controls to the GY")]
    public void PickingZones_ChangesTheWording(Zone zones, string expected)
    {
        var b = BlockRegistry.Find("do.send_to_gy")!.CreateInstance();
        b.Values["count"] = "3";
        b.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = zones };
        var text = BlockRegistry.Find("do.send_to_gy")!.Describe(new BlockArgs(BlockRegistry.Find("do.send_to_gy")!, b));
        Assert.Equal(expected, System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim());
    }

    [Fact]
    public void ACardWithNoText_IsSavedWithItsSuggestedText()
    {
        using var tmp = new TempDir();
        var pack = new CardPack { Name = "P", Cards = [Samples.PotOfGreedClone()] };
        pack.Cards[0].Card.Text = "";
        var result = CardCrafter.Core.Omega.PackExporter.Export(pack, tmp.Path);
        var row = CardCrafter.Core.Database.CdbFile.Read(result.DatabasePath).Single();
        Assert.Equal("Draw 2 cards.", row.Desc);
    }
}

public class OncePerTurnWordingTests
{
    private static EffectEvent Effect(OncePerTurn limit, int group = 0) => new()
    {
        When = BlockRegistry.Find("when.ignition")!.CreateInstance(),
        Do = [BlockRegistry.Find("do.draw")!.CreateInstance()],
        Limit = limit,
        SharedLimitGroup = group,
    };

    private static CardProject Card(params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = 100006001, Name = "New Monster", Frame = MonsterFrame.Effect, Level = 4 },
        Effects = [.. effects],
    };

    [Fact]
    public void TwoHardLimits_SayEachEffectOnce()
    {
        var text = CardCrafter.Core.Text.EffectTextGenerator.Generate(Card(Effect(OncePerTurn.Hard), Effect(OncePerTurn.Hard)));
        Assert.Equal("You can draw 1 card. You can draw 1 card. You can only use each effect of \"New Monster\" once per turn.", text);
    }

    [Fact]
    public void ASharedLimit_SaysOneOfTheseEffects()
    {
        var text = CardCrafter.Core.Text.EffectTextGenerator.Generate(Card(Effect(OncePerTurn.Hard, 1), Effect(OncePerTurn.Hard, 1)));
        Assert.EndsWith("You can only use 1 of these effects of \"New Monster\" per turn, and only once that turn.", text);
    }

    [Fact]
    public void OneHardLimit_KeepsThisEffect()
    {
        var text = CardCrafter.Core.Text.EffectTextGenerator.Generate(Card(Effect(OncePerTurn.Hard), Effect(OncePerTurn.Soft)));
        Assert.Equal("You can draw 1 card. You can only use this effect of \"New Monster\" once per turn. Once per turn: You can draw 1 card.", text);
    }
}

public class ThisCardPickerTests
{
    private static CardProject Card(CardFilter filter) => new()
    {
        Card = new CardData { Id = 100007001, Name = "New Monster", Frame = MonsterFrame.Effect, Level = 4, Atk = 1800, Def = 1000 },
        Effects =
        [
            new EffectEvent
            {
                Limit = OncePerTurn.Hard,
                When = BlockRegistry.Find("when.quick")!.CreateInstance().With("zone", "hand"),
                Do = [BlockRegistry.Find("do.special_summon")!.CreateInstance().With("cards", filter)],
            },
        ],
    };

    [Fact]
    public void ThisCardItself_ReadsAndCompilesAsThisCard()
    {
        var p = Card(new CardFilter { ThisCard = true });
        Assert.Equal("(Quick Effect): You can Special Summon this card from your hand. You can only use this effect of \"New Monster\" once per turn.",
            CardCrafter.Core.Text.EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
        Assert.Contains("Group.FromCards(c)", r.Lua);
        Assert.Contains("e:GetHandler():IsCanBeSpecialSummoned(e,0,tp,false,false)", r.Lua);
    }

    [Fact]
    public void PickingItsOwnName_WarnsThatAnyCopyCounts()
    {
        var p = Card(new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, CardCode = 100007001 });
        Assert.Contains(ScriptCompiler.Compile(p).Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("This card itself"));
    }

    [EngineFact]
    public void ThisCardItself_SummonsThisCopy()
    {
        var p = Card(new CardFilter { ThisCard = true });
        p.Effects[0].Limit = OncePerTurn.None;
        new EngineScenario(p).Card(p.Card.Id, 0, "hand", 2).Card(EngineScenario.Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id})==1")
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_HAND,0,nil,{p.Card.Id})==1")
            .AssertPasses();
    }

    [Fact]
    public void ExceptItsOwnName()
    {
        var f = new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Warrior, ExceptOwnName = true };
        var p = Card(f);
        p.Effects[0].Do[0] = BlockRegistry.Find("do.add_to_hand")!.CreateInstance().With("cards", f);
        Assert.Contains("add 1 Warrior monster from your Deck to your hand, except \"New Monster\"", CardCrafter.Core.Text.EffectTextGenerator.Generate(p));
        Assert.Contains("not c:IsCode(id)", ScriptCompiler.Compile(p).Lua);
    }
}

public class BlockModeTests
{
    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static CardProject Card(params BlockInstance[] actions) => new()
    {
        Card = new CardData { Id = 100008001, Name = "Sweeper", Frame = MonsterFrame.Effect, Level = 4 },
        Effects = [new EffectEvent { When = B("when.ignition"), Do = [.. actions] }],
    };

    [Fact]
    public void UpToAndAll_ReadAsCardsSayThem()
    {
        var upTo = B("do.send_to_gy").With("count", 3).With("mode", "up_to").With("cards", CardFilter.OpponentField());
        Assert.Equal("You can send up to 3 cards your opponent controls to the GY.", CardCrafter.Core.Text.EffectTextGenerator.Generate(Card(upTo)));
        Assert.Contains(",1,3,", ScriptCompiler.Compile(Card(upTo)).Lua);
        var all = B("do.destroy").With("mode", "all").With("cards", CardFilter.OpponentField(FilterCardType.Monster) );
        all.Filters["cards"].Zones = Zone.MonsterZone;
        Assert.Equal("You can destroy all monsters your opponent controls.", CardCrafter.Core.Text.EffectTextGenerator.Generate(Card(all)));
        Assert.Contains("Duel.GetMatchingGroup(", ScriptCompiler.Compile(Card(all)).Lua);
    }

    [Fact]
    public void SpecialSummoningAll_IsRefused()
    {
        var p = Card(B("do.special_summon").With("mode", "all"));
        Assert.Contains(ScriptCompiler.Compile(p).Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("all"));
    }

    [Fact]
    public void ForEachCardUsedForTheCost()
    {
        var p = Card(B("do.damage").With("amount", 300).With("per", "per_cost"));
        p.Effects[0].Cost.Add(B("cost.move_cards").With("how", "grave").With("count", 3).With("mode", "up_to").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand }));
        Assert.Equal("You can send up to 3 cards from your hand to the GY; inflict 300 damage to your opponent for each card used for the cost.",
            CardCrafter.Core.Text.EffectTextGenerator.Generate(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("e:SetLabel(Duel.SendtoGrave(", lua);
        Assert.Contains("Duel.Damage(1-tp,300*e:GetLabel(),REASON_EFFECT)", lua);
    }

    [EngineFact]
    public void ForEachOfThoseCards_CountsTheActionBefore()
    {
        var destroy = B("do.destroy").With("mode", "all").With("cards", CardFilter.OpponentField(FilterCardType.Monster));
        destroy.Filters["cards"].Zones = Zone.MonsterZone;
        var p = Card(destroy, B("do.damage").With("amount", 300).With("per", "per_previous"));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(EngineScenario.Fiend, 1, "mzone", 3).Card(EngineScenario.Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0")
            .Check("Duel.GetLP(1)==8000-900")
            .AssertPasses();
    }

    [EngineFact]
    public void UpToThree_SendsWhatThereIs()
    {
        var p = Card(B("do.send_to_gy").With("count", 3).With("mode", "up_to").With("cards", CardFilter.OpponentField()));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(EngineScenario.Fiend, 1, "mzone", 2).Card(EngineScenario.Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)>=1")
            .AssertPasses();
    }
}
