using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Blocks and filter options added in the sixth sweep of the official scripts.</summary>
public class SweepRound6Tests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Lasting(long id, BlockInstance block, params BlockInstance[] conditions) => new()
    {
        Card = new CardData { Id = id, Name = "Card " + id, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [new EffectEvent { When = B("when.while_face_up"), If = [.. conditions], Do = [block] }],
    };

    [EngineFact]
    public void RevealCost_ShowsAMonsterInTheHand_ThenDraws()
    {
        var reveal = B(RevealCost.BlockId);
        reveal.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, ExcludeThisCard = true };
        var p = new CardProject
        {
            Card = new CardData { Id = 100014000, Name = "Revealer", Level = 4 },
            Effects = [new EffectEvent { When = B("when.ignition", ("zone", "hand")), Cost = [reveal], Do = [B("do.draw")] }],
        };
        Assert.Equal("If this card is in your hand: You can reveal 1 other monster in your hand; draw 1 card.", EffectTextGenerator.Generate(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("Duel.ConfirmCards(1-tp,", lua);
        Assert.Contains("not c:IsPublic()", lua);
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(p.Card.Id).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==3").AssertPasses();
        // Nothing else to reveal: it can't be activated.
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(p.Card.Id).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void ExtraSummon_LetsYouNormalSummonTwice()
    {
        var extra = B(ExtraSummonPassive.BlockId);
        extra.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, Race = CardRace.Warrior };
        var p = Lasting(100014010, extra);
        Assert.Equal("You can Normal Summon 1 Warrior monster in addition to your Normal Summon/Set.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "hand", 2).Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Summon(Warrior).Summon(Warrior)
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior})==2").AssertPasses();
    }

    [EngineFact]
    public void SelfDestroy_WhenItsConditionHolds()
    {
        var none = B("if.no_cards");
        none.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true };
        var p = Lasting(100014020, B(SelfDestroyPassive.BlockId), none);
        Assert.Equal("If you control no other monsters, destroy this card.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3).Turns(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{p.Card.Id})").AssertPasses();
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3).Turns(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})").AssertPasses();
    }

    [Fact]
    public void SelfDestroy_WithoutConditions_Warns()
    {
        var r = ScriptCompiler.Compile(Lasting(100014021, B(SelfDestroyPassive.BlockId)));
        Assert.Contains(r.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("no IF blocks"));
        Assert.Contains("EFFECT_SELF_DESTROY", r.Lua);
    }

    [Fact]
    public void MaterialBan_WithAndWithoutAnException()
    {
        var plain = Lasting(100014030, B(MaterialBanPassive.BlockId, ("kind", "link")));
        Assert.Equal("Cannot be used as Link Material.", EffectTextGenerator.Generate(plain));
        var lua = ScriptCompiler.Compile(plain).Lua;
        Assert.Contains("EFFECT_CANNOT_BE_LINK_MATERIAL", lua);
        Assert.Contains("SetValue(1)", lua);

        var ban = B(MaterialBanPassive.BlockId, ("kind", "synchro"));
        ban.Filters["cards"] = new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Water };
        var water = Lasting(100014031, ban);
        Assert.Equal("Cannot be used as Synchro Material, except for the Synchro Summon of a WATER monster.", EffectTextGenerator.Generate(water));
        var r = ScriptCompiler.Compile(water);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
        Assert.Contains("EFFECT_CANNOT_BE_SYNCHRO_MATERIAL", r.Lua);
        Assert.Contains("ATTRIBUTE_WATER", r.Lua);
    }

    [Fact]
    public void BattledThisTurn_Condition()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100014040, Name = "Battler", Level = 4 },
            Effects = [new EffectEvent { When = B("when.phase", ("phase", "end")), If = [B("if.battled_this_turn")], Do = [B("do.draw")] }],
        };
        Assert.Contains("this card battled this turn", EffectTextGenerator.Generate(p));
        Assert.Contains("GetBattledGroupCount()>0", ScriptCompiler.Compile(p).Lua);
    }

    [Theory]
    [InlineData(FilterPosition.FaceUpAttack, "face-up Attack Position monster", "IsPosition(POS_FACEUP_ATTACK)")]
    [InlineData(FilterPosition.Defense, "Defense Position monster", "IsDefensePos()")]
    [InlineData(FilterPosition.FaceDownDefense, "face-down Defense Position monster", "IsPosition(POS_FACEDOWN_DEFENSE)")]
    public void FilterPositions(FilterPosition position, string text, string check)
    {
        var f = new CardFilter { CardType = FilterCardType.Monster, Zones = Zone.MonsterZone, Owner = CardOwner.Opponent, Position = position };
        Assert.Equal(text + " your opponent controls", FilterText.Describe(f, null));
        Assert.Contains(FilterCompiler.Conditions(f), c => c.Contains(check));
    }

    [Fact]
    public void FilterKindsSummonsStatsNamesAndMentions()
    {
        var field = new CardFilter { CardType = FilterCardType.Spell, SpellTrapKind = SpellTrapKind.Field, Zones = Zone.Deck, Owner = CardOwner.You };
        Assert.Equal("Field Spell from your Deck", FilterText.Describe(field, null));
        Assert.Contains(FilterCompiler.Conditions(field), c => c.Contains("TYPE_FIELD"));

        var extra = new CardFilter { CardType = FilterCardType.Monster, Zones = Zone.MonsterZone, Owner = CardOwner.Opponent, Summoned = FilterSummoned.FromExtra };
        Assert.Equal("monster Special Summoned from the Extra Deck your opponent controls", FilterText.Describe(extra, null));
        Assert.Contains(FilterCompiler.Conditions(extra), c => c.Contains("IsSummonLocation(LOCATION_EXTRA)"));

        var zero = new CardFilter { CardType = FilterCardType.Monster, AtkMin = 0, AtkMax = 0, DefMin = 0, DefMax = 0 };
        Assert.Equal("monster with 0 ATK and 0 DEF", FilterText.Describe(zero, null, withLocation: false));

        CardNames.Set([(46986414, "Dark Magician"), (38033121, "Dark Magician Girl")]);
        try
        {
            var either = new CardFilter { CardCode = 46986414, AltCodes = [38033121] };
            Assert.Equal("\"Dark Magician\" or \"Dark Magician Girl\"", FilterText.Describe(either, null, withLocation: false));
            Assert.Contains(FilterCompiler.Conditions(either), c => c.Contains("IsCode(46986414,38033121)"));
            var mentions = new CardFilter { MentionsCode = 46986414, MentionsOrIs = true, Zones = Zone.Deck, Owner = CardOwner.You };
            Assert.Equal("card that is \"Dark Magician\" or mentions it from your Deck", FilterText.Describe(mentions, null));
            Assert.Equal("cards with passcode 1", FilterText.Describe(new CardFilter { CardCode = 1 }, null, plural: true, withLocation: false));
        }
        finally { CardNames.Set([]); }

        var two = new CardFilter { CardType = FilterCardType.Monster, Archetype = 0x3b, Archetype2 = 0x10a2 };
        Assert.Contains(FilterCompiler.Conditions(two), c => c.Contains("IsSetCard(0x3b)") && c.Contains("IsSetCard(0x10a2)"));
    }

    [EngineTheory]
    [InlineData(3493978L)]  // Don Turtle: up to as many as you have free zones
    [InlineData(37132349L)] // Ojama Red: up to 4
    public void UpToFreeZones_Translates(long id)
    {
        var dir = EngineScripts!;
        var lua = File.ReadAllText(Path.Combine(dir, $"c{id}.lua"));
        var card = new CardData { Id = id, Name = "Card", Level = 2 };
        var result = ScriptTranslator.Translate(lua, id, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var project = new CardProject { Card = card, Effects = result.Pieces.SelectMany(p => p.Effects).ToList() };
        Assert.Contains("You can Special Summon up to ", EffectTextGenerator.Generate(project));
        Assert.False(ScriptCompiler.Compile(project).HasErrors);
    }
}
