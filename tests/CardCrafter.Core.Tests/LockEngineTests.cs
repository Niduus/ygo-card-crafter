using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Lock-outs and floodgates (Ryzeal, Vanity's Emptiness, Spell Canceller, Macro Cosmos, Dimension Shifter...) in real duels.</summary>
public class LockEngineTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance Except(BlockInstance b, CardFilter f) { b.Filters["cards"] = f; return b; }

    private static CardFilter AnyMonster => new() { CardType = FilterCardType.Monster };

    private static CardProject Floodgate(long id, params BlockInstance[] passives) => new()
    {
        Card = new CardData { Id = id, Name = "Gate " + id, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [.. passives] }],
    };

    /// <summary>A monster that Special Summons itself from the hand by its own procedure.</summary>
    private static CardProject Summoner(long id, CardAttribute attribute = CardAttribute.Light, params BlockInstance[] costs) => new()
    {
        Card = new CardData { Id = id, Name = "Summoner " + id, Level = 4, Attribute = attribute },
        Effects = [new EffectEvent { When = B("when.summon_procedure", ("zone", "hand")), Cost = [.. costs] }],
    };

    private static CardProject Linker(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Linker", Frame = MonsterFrame.Link, Atk = 1600, LinkMarkers = LinkMarker.Left | LinkMarker.Right },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2 },
    };

    private static string InHand(long id) => $"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{id})";
    private static string OnField(long id) => $"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})";

    [EngineFact]
    public void VanitysEmptiness_NeitherPlayerCanSpecialSummon()
    {
        var gate = Floodgate(100007000, Except(B("passive.lock", ("who", "both"), ("what", "special")), AnyMonster));
        var s = Summoner(100007001);
        new EngineScenario(gate, s).Card(gate.Card.Id, 0, "mzone").Card(s.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .SpSummon(s.Card.Id).Check(InHand(s.Card.Id)).AssertPasses();
    }

    [EngineFact]
    public void BarrierStatue_OnlyTheExceptedAttributeCanBeSpecialSummoned()
    {
        var gate = Floodgate(100007010, Except(B("passive.lock", ("who", "both"), ("what", "special")),
            new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Wind }));
        var light = Summoner(100007011, CardAttribute.Light);
        var wind = Summoner(100007012, CardAttribute.Wind);
        new EngineScenario(gate, light, wind).Card(gate.Card.Id, 0, "mzone").Card(light.Card.Id, 0, "hand").Card(wind.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .SpSummon(light.Card.Id).SpSummon(wind.Card.Id)
            .Check(InHand(light.Card.Id)).Check(OnField(wind.Card.Id)).AssertPasses();
        Assert.Equal("Neither player can Special Summon monsters, except WIND monsters.", EffectTextGenerator.Generate(gate));
    }

    [EngineFact]
    public void PalmRyzeal_NoExtraDeckSummonsExceptRank4Xyz()
    {
        var gate = Floodgate(100007020, Except(B("passive.lock", ("who", "you"), ("what", "special_extra")),
            new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz, LevelMin = 4, LevelMax = 4 }));
        var link = Linker(100007021);
        new EngineScenario(gate, link).Card(gate.Card.Id, 0, "mzone").Card(EffectFiller, 0, "mzone", 2).Card(link.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 5)
            .SpSummon(link.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_EXTRA,0,1,nil,{link.Card.Id})").AssertPasses();
        Assert.Equal("You cannot Special Summon from the Extra Deck, except Rank 4 Xyz Monsters.", EffectTextGenerator.Generate(gate));
        Assert.Contains("c:IsRank(4)", ScriptCompiler.Compile(gate).Lua);
    }

    [EngineFact]
    public void MajestysFiend_NeitherPlayerCanActivateMonsterEffects()
    {
        var gate = Floodgate(100007030, Except(B("passive.lock", ("who", "both"), ("what", "activate_monster")), AnyMonster));
        var drawer = new CardProject
        {
            Card = new CardData { Id = 100007031, Name = "Drawer", Level = 4 },
            Effects = [new EffectEvent { When = B("when.ignition"), Do = [B("do.draw")] }],
        };
        new EngineScenario(gate, drawer).Card(gate.Card.Id, 0, "mzone").Card(drawer.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(drawer.Card.Id).ExpectActivations(0).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    [EngineFact]
    public void SpellCanceller_SpellsCannotBeActivated()
    {
        var gate = Floodgate(100007040,
            Except(B("passive.lock", ("who", "both"), ("what", "activate_spell")), AnyMonster),
            B("passive.negate_all", ("what", "spell")));
        var spell = new CardProject
        {
            Card = new CardData { Id = 100007041, Name = "Draw Spell", Kind = CardKind.Spell, SpellKind = SpellKind.Normal },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.draw")] }],
        };
        new EngineScenario(gate, spell).Card(gate.Card.Id, 0, "mzone").Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Activate(spell.Card.Id).ExpectActivations(0).Check(InHand(spell.Card.Id)).AssertPasses();
        Assert.Equal("Spell Cards, and their effects on the field, cannot be activated. Negate all Spell effects on the field.", EffectTextGenerator.Generate(gate));
    }

    [EngineFact]
    public void MacroCosmos_DestroyedMonstersAreBanishedInstead()
    {
        var gate = Floodgate(100007050, B("passive.banish_instead", ("what", "card"), ("whose", "either")));
        var destroyer = new CardProject
        {
            Card = new CardData { Id = 100007051, Name = "Destroyer", Level = 4 },
            Effects = [new EffectEvent { When = B("when.ignition"), Do = [B("do.destroy")] }],
        };
        destroyer.Effects[0].Do[0].Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        new EngineScenario(gate, destroyer).Card(gate.Card.Id, 0, "mzone").Card(destroyer.Card.Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(destroyer.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)==1").Check("Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==0").AssertPasses();
        Assert.Equal("Any card sent to the GY is banished instead.", EffectTextGenerator.Generate(gate));
    }

    [EngineFact]
    public void DimensionShifter_BanishesInsteadAfterItResolves()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100007060, Name = "Shifter", Level = 4 },
            Effects = [new EffectEvent { When = B("when.ignition"), Do = [B("do.banish_instead", ("duration", "next")), B("do.destroy")] }],
        };
        p.Effects[0].Do[1].Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1).Check("Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)==1").AssertPasses();
        Assert.Contains("until the end of the next turn, any card sent to the GY is banished instead", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void TurnYouActivateThis_StopsTheEffectAfterAForbiddenSummon()
    {
        // "You cannot Special Summon the turn you activate this effect, except DARK monsters." The effect needs 2 monsters,
        // so it can only be activated after the other monster is Special Summoned.
        var twoMonsters = B("if.cards_exist", ("count", "2"));
        twoMonsters.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        var p = new CardProject
        {
            Card = new CardData { Id = 100007070, Name = "Locker", Level = 4 },
            Effects = [new EffectEvent
            {
                When = B("when.ignition"),
                If = [twoMonsters],
                Cost = [Except(B("cost.summon_restriction", ("what", "special"), ("when", "whole_turn")), new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark })],
                Do = [B("do.draw")],
            }],
        };
        var light = Summoner(100007071, CardAttribute.Light);
        var dark = Summoner(100007072, CardAttribute.Dark);
        // A LIGHT monster was Special Summoned this turn: the effect can't be activated.
        new EngineScenario(p, light).Card(p.Card.Id, 0, "mzone").Card(light.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .SpSummon(light.Card.Id).Activate(p.Card.Id).ExpectActivations(0).Check(OnField(light.Card.Id)).AssertPasses();
        // A DARK monster is allowed.
        new EngineScenario(p, dark).Card(p.Card.Id, 0, "mzone").Card(dark.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .SpSummon(dark.Card.Id).Activate(p.Card.Id).ExpectActivations(1).AssertPasses();
        Assert.Contains("You cannot Special Summon monsters the turn you activate this effect, except DARK monsters.", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void RyzealProcedure_LocksTheExtraDeckIfSummonedThisWay()
    {
        var ryzeal = Summoner(100007080, CardAttribute.Fire,
            Except(B("cost.summon_restriction", ("what", "special_extra"), ("when", "rest")),
                new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz, LevelMin = 4, LevelMax = 4 }));
        ryzeal.Effects[0].Limit = OncePerTurn.Hard;
        var link = Linker(100007081);
        new EngineScenario(ryzeal, link).Card(ryzeal.Card.Id, 0, "hand").Card(EffectFiller, 0, "mzone", 2).Card(link.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 5)
            .SpSummon(ryzeal.Card.Id).SpSummon(link.Card.Id)
            .Check(OnField(ryzeal.Card.Id)).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_EXTRA,0,1,nil,{link.Card.Id})").AssertPasses();
        var text = EffectTextGenerator.Generate(ryzeal);
        Assert.True(text.Contains("If Summoned this way, you cannot Special Summon from the Extra Deck for the rest of this turn, except Rank 4 Xyz Monsters.", StringComparison.Ordinal), text);
    }

    [Fact]
    public void LockWording()
    {
        string Do(string who, string what, CardFilter except)
        {
            var b = Except(B("do.summon_restriction", ("who", who), ("what", what)), except);
            return EffectTextGenerator.Describe(new EffectEvent { When = B("when.ignition"), Do = [b] }, new CardData { Name = "X" });
        }
        Assert.Contains("for the rest of this turn, your opponent cannot activate cards or effects", Do("opponent", "activate_any", AnyMonster));
        Assert.Contains("you cannot declare attacks, except with Rank 4 Xyz Monsters",
            Do("you", "attack", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz, LevelMin = 4, LevelMax = 4 }));
        var mistake = Floodgate(100007090, B("passive.lock", ("who", "both"), ("what", "search")));
        Assert.Equal("Neither player can add cards from their Deck to their hand except by drawing them.", EffectTextGenerator.Generate(mistake));
        var drain = Floodgate(100007091, B("passive.negate_all", ("what", "monster")));
        Assert.Equal("Negate the effects of all face-up monsters on the field.", EffectTextGenerator.Generate(drain));
        var law = Floodgate(100007092, B("passive.banish_instead", ("what", "card"), ("whose", "opponent")));
        Assert.Equal("Any card sent to your opponent's GY is banished instead.", EffectTextGenerator.Generate(law));
    }
}

/// <summary>The official lock-outs and floodgates read as the lock blocks.</summary>
public class LockTranslationTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static CardData Real(long id, CardKind kind = CardKind.Monster, SpellKind spell = SpellKind.Normal, TrapKind trap = TrapKind.Normal) =>
        new() { Id = id, Name = "Card " + id, Kind = kind, SpellKind = spell, TrapKind = trap, Level = 4 };

    [EngineTheory]
    [InlineData(5851097L, "passive.lock")]        // Vanity's Emptiness (its self-destruction stays script)
    [InlineData(33746252L, "passive.lock")]       // Majesty's Fiend
    [InlineData(73356503L, "passive.lock")]       // Barrier Statue of the Stormwinds
    [InlineData(59305593L, "passive.lock")]       // Mistake
    [InlineData(68937720L, "passive.lock")]       // Mind Drain
    [InlineData(73599290L, "passive.lock")]       // Soul Drain
    [InlineData(84636823L, "passive.lock")]       // Spell Canceller
    [InlineData(61116514L, "passive.lock")]       // Palm Ryzeal
    [InlineData(81674782L, "passive.banish_instead")] // Dimensional Fissure
    [InlineData(30241314L, "passive.banish_instead")] // Macro Cosmos
    [InlineData(48626373L, "passive.banish_instead")] // Kashtira Arise-Heart
    [InlineData(82732705L, "passive.negate_all")] // Skill Drain
    [InlineData(61740673L, "passive.negate_all")] // Imperial Order
    [InlineData(51452091L, "passive.negate_all")] // Royal Decree
    [InlineData(91800273L, "do.banish_instead")]  // Dimension Shifter
    public void Floodgates_TranslateToLockBlocks(long id, string block)
    {
        var result = ScriptTranslator.Translate(Script(id), id, Real(id));
        var blocks = result.Pieces.SelectMany(p => p.Effects).SelectMany(e => e.Do).Select(b => b.BlockId).ToList();
        Assert.True(blocks.Contains(block), $"{string.Join(", ", blocks)}\n{string.Join("\n", ScriptTranslator.Untranslated(result))}");
    }

    [EngineFact]
    public void SpellCanceller_TranslatesCompletely()
    {
        var result = ScriptTranslator.Translate(Script(84636823), 84636823, Real(84636823));
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var project = new CardProject { Card = Real(84636823), Effects = result.Pieces.SelectMany(p => p.Effects).ToList() };
        Assert.Equal("Spell Cards, and their effects on the field, cannot be activated. Negate all Spell effects on the field.", EffectTextGenerator.Generate(project));
    }

    [EngineFact]
    public void IceRyzeal_ProcedurePaysAndLocksTheExtraDeck()
    {
        var card = Real(8633261);
        var result = ScriptTranslator.Translate(Script(8633261), 8633261, card);
        var proc = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.When?.BlockId == "when.summon_procedure");
        Assert.True(proc is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        Assert.Contains(proc!.Cost, b => b.BlockId == "cost.move_cards");
        var lockCost = Assert.Single(proc.Cost, b => b.BlockId == "cost.summon_restriction");
        Assert.Equal("special_extra", lockCost.Values["what"]);
        Assert.Equal(MonsterKind.Xyz, lockCost.Filters["cards"].MonsterKind);
        Assert.Equal(4, lockCost.Filters["cards"].LevelMin);
        Assert.Equal(OncePerTurn.Hard, proc.Limit);
        var text = EffectTextGenerator.Describe(proc, new CardData { Name = "Ice Ryzeal", Level = 4 });
        Assert.Contains("If Summoned this way, you cannot Special Summon from the Extra Deck for the rest of this turn, except Rank 4 Xyz Monsters.", text);
    }
}
