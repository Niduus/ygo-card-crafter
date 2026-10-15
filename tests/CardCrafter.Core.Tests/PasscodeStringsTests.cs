using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The game finds a card's effect texts through aux.Stringid(id,n) = id*16+n, kept in 32 bits: passcodes above
/// 268435455 point at another card, and prompts show "hand" instead of the effect.
/// </summary>
public class PasscodeStringsTests
{
    [Fact]
    public void GeneratedPasscodes_AlwaysFitTheEffectTexts()
    {
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var id = PasscodeGenerator.Generate([], rng);
            Assert.InRange(id, PasscodeGenerator.First, PasscodeGenerator.MaxForStrings);
            Assert.True((id * 16 + 15) <= uint.MaxValue);
        }
    }

    [Fact]
    public void TooLargePasscodes_AreWarnedAbout()
    {
        CardProject Card(long id) => new() { Card = new CardData { Id = id, Name = "X", Level = 4 } };
        Assert.Contains(ScriptCompiler.Compile(Card(995260000)).Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("too large for the game's effect texts"));
        Assert.DoesNotContain(ScriptCompiler.Compile(Card(268430000)).Diagnostics, d => d.Message.Contains("too large"));
    }

    [Fact]
    public void FixAll_RenumbersTooLargeCards_AndTheCardsThatNameThem()
    {
        var big = new CardProject { Card = new CardData { Id = 995260000, Name = "New Monster", Level = 4 } };
        var add = BlockRegistry.Find("do.add_to_hand")!.CreateInstance();
        add.Filters["cards"] = new CardFilter { CardCode = 995260000, Zones = Zone.Deck, Owner = CardOwner.You };
        var searcher = new CardProject
        {
            Card = new CardData { Id = 200000000, Name = "Searcher", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = BlockRegistry.Find("when.activate")!.CreateInstance(), Do = [add] }],
        };
        var script = new CardProject
        {
            Card = new CardData { Id = 210000000, Name = "Scripted", Level = 4 }, UseCustomLua = true,
            CustomLua = "local s,id=c210000000,210000000\nfunction s.initial_effect(c)\n\taux.AddCodeList(c,995260000)\nend\n",
        };
        var pack = new CardPack { Cards = [big, searcher, script] };
        var map = PasscodeFix.Apply(pack);
        var now = Assert.Single(map).Value;
        Assert.Equal(now, big.Card.Id);
        Assert.InRange(now, PasscodeGenerator.First, PasscodeGenerator.MaxForStrings);
        Assert.Equal(now, add.Filters["cards"].CardCode);
        Assert.Contains($"aux.AddCodeList(c,{now})", script.CustomLua);
        Assert.Equal(200000000, searcher.Card.Id);
        Assert.Empty(PasscodeFix.Apply(pack));
    }

    [EngineFact]
    public void InTheEngine_TheDescriptionPointsAtTheCard_OnlyWhenThePasscodeFits()
    {
        // The engine's own value for SetDescription(aux.Stringid(id,0)), read back and split into card and index.
        foreach (var (id, fits) in new[] { (268430000L, true), (995260000L, false) })
        {
            var p = new CardProject
            {
                Card = new CardData { Id = id, Name = "Desc", Level = 4 },
                Effects = [new EffectEvent { When = BlockRegistry.Find("when.ignition")!.CreateInstance(), Do = [BlockRegistry.Find("do.draw")!.CreateInstance()] }],
            };
            var check = $"Duel.GetFieldCard(0,LOCATION_MZONE,0):IsHasEffect(0)==nil and (function() local e=Effect.CreateEffect(Duel.GetFieldCard(0,LOCATION_MZONE,0)) " +
                        $"e:SetDescription(aux.Stringid({id},0)) return (e:GetDescription()>>4)=={id} end)()=={(fits ? "true" : "false")}";
            new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3).Check(check).AssertPasses();
        }
    }
}
