using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Blocks added while sweeping the official scripts: protection once per turn, attack targets, no-Tribute Summons, names, "this way".</summary>
public class SweepRound5Tests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Lasting(long id, BlockInstance block, int level = 4, params BlockInstance[] conditions) => new()
    {
        Card = new CardData { Id = id, Name = "Card " + id, Level = level, Atk = 1000, Def = 1000 },
        Effects = [new EffectEvent { When = B("when.while_face_up"), If = [.. conditions], Do = [block] }],
    };

    [EngineFact]
    public void NotDestroyedOnce_SavesItTheFirstTimeOnly()
    {
        var tough = Lasting(100013000, B(NotDestroyedOncePassive.BlockId, ("by", "effect")));
        var destroy = B("do.destroy");
        destroy.Filters["cards"] = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardCode = tough.Card.Id };
        var destroyer = new CardProject
        {
            Card = new CardData { Id = 100013001, Name = "Destroyer", Level = 4 },
            Effects = [new EffectEvent { When = B("when.ignition"), Do = [destroy] }],
        };
        new EngineScenario(tough, destroyer).Card(tough.Card.Id, 0, "mzone").Card(destroyer.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(destroyer.Card.Id, 2).ExpectActivations(2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{tough.Card.Id})").AssertPasses();
        Assert.Equal("Once per turn, if this card would be destroyed by card effect, it is not destroyed.", EffectTextGenerator.Generate(tough));
    }

    [EngineFact]
    public void NoTributeSummon_SummonsALevel7WithoutTributes()
    {
        var big = Lasting(100013010, B(NoTributeSummonPassive.BlockId), level: 7);
        new EngineScenario(big).Card(big.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Summon(big.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{big.Card.Id})").AssertPasses();
        Assert.Equal("You can Normal Summon this card without Tributing.", EffectTextGenerator.Generate(big));
    }

    [Fact]
    public void NoTributeSummon_WithACondition_ReadsAndChecksIt()
    {
        var cond = B("if.no_cards");
        cond.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        var big = Lasting(100013011, B(NoTributeSummonPassive.BlockId), 7, cond);
        Assert.Equal("If you control no monsters, you can Normal Summon this card without Tributing.", EffectTextGenerator.Generate(big));
        var lua = ScriptCompiler.Compile(big).Lua;
        Assert.Contains("EFFECT_SUMMON_PROC", lua);
        Assert.Contains("minc==0", lua);
        Assert.Contains("not Duel.IsExistingMatchingCard(", lua);
    }

    [Fact]
    public void AttackTargets_NameBecomes_AndImmuneChoices_Compile()
    {
        var at = Lasting(100013020, B(AttackTargetsPassive.BlockId));
        Assert.Equal("Your opponent cannot target other monsters you control for attacks.", EffectTextGenerator.Generate(at));
        Assert.Contains("EFFECT_CANNOT_SELECT_BATTLE_TARGET", ScriptCompiler.Compile(at).Lua);
        var name = B(NameBecomesPassive.BlockId, ("where", "field_gy"));
        name.Filters["card"] = new CardFilter { CardCode = 46986414 };
        var named = Lasting(100013021, name);
        CardNames.Set([(46986414, "Dark Magician")]);
        try { Assert.Equal("This card's name becomes \"Dark Magician\" while on the field or in the GY.", EffectTextGenerator.Generate(named)); }
        finally { CardNames.Set([]); }
        Assert.Contains("SetValue(46986414)", ScriptCompiler.Compile(named).Lua);
        foreach (var whose in new[] { "spell", "trap", "opp_spell", "opp_trap", "opp_activated" })
        {
            var r = ScriptCompiler.Compile(Lasting(100013022, B("passive.immune", ("whose", whose))));
            Assert.False(r.HasErrors, whose);
        }
    }

    [Fact]
    public void SelfStatsPer_CountsMaterialsLevelsAndLinks()
    {
        foreach (var (per, text) in new[] { ("materials", "for each material attached to it"), ("level", "for each of its Levels"), ("linked", "for each monster it points to") })
        {
            var p = Lasting(100013030, B("passive.self_stats_per", ("per", per), ("amount", "200")));
            Assert.Contains("This card gains 200 ATK " + text, EffectTextGenerator.Generate(p));
            Assert.False(ScriptCompiler.Compile(p).HasErrors, per);
        }
    }

    [EngineFact]
    public void SummonedByItsOwnEffect_IsTrueAfterSpecialSummoningItself()
    {
        // "You can Special Summon this card from your hand. If this card is Special Summoned by its own effect: Draw 1 card."
        var p = new CardProject
        {
            Card = new CardData { Id = 100013040, Name = "Self", Level = 4 },
            Effects =
            [
                new EffectEvent { When = B("when.ignition", ("zone", "hand")), Do = [B("do.special_summon_self")] },
                new EffectEvent { When = B("when.summoned", ("how", "self"), ("optional", "false")), Do = [B("do.draw")] },
            ],
        };
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(p.Card.Id).Turns(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
        Assert.Contains("If this card is Special Summoned by its own effect: Draw 1 card.", EffectTextGenerator.Generate(p));
    }
}

public class CardCodesTests
{
    [Fact]
    public void Codes_ListPasscodeTypeAttributeAndArchetypes()
    {
        var card = new CardData { Id = 46986414, Name = "Dark Magician", Level = 7, Race = CardRace.Spellcaster, Attribute = CardAttribute.Dark, Frame = MonsterFrame.Normal, Archetypes = [0x10a2, 0x13a] };
        var pack = new CardPack { Archetypes = { new Archetype { Code = 0x10a2, Name = "Dark Magician" } } };
        var codes = CardCodes.Of(card, pack);
        Assert.Contains(codes, c => c is { Label: "Passcode", Value: "46986414" });
        Assert.Contains(codes, c => c is { Label: "Type", Value: "0x2", Note: "Spellcaster" });
        Assert.Contains(codes, c => c is { Label: "Attribute", Value: "0x20", Note: "DARK" });
        Assert.Contains(codes, c => c is { Label: "Archetype", Value: "0x10a2", Note: "Dark Magician" });
        Assert.Contains(codes, c => c is { Label: "Archetype", Value: "0x13a", Note: "" });
        Assert.Contains(codes, c => c.Label == "Setcode (all)" && c.Value == "0x13a10a2");
        Assert.Contains(codes, c => c is { Label: "Card type", Value: "0x11" });
    }
}
