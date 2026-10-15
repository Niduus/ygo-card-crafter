using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

public class TextTests
{
    [Fact]
    public void DragonCommander_ReadsLikeCardText()
    {
        var p = Samples.DragonCommander();
        var lines = p.Effects.Select(e => EffectTextGenerator.Describe(e, p.Card, Samples.Pack(p))).ToList();
        // On the card the effects run together in one paragraph.
        Assert.Equal(string.Join(" ", lines), EffectTextGenerator.Generate(p, Samples.Pack(p)));
        Assert.Equal(
            "If this card is Normal or Special Summoned: You can add 1 Level 4 or lower Dragon monster from your Deck to your hand. " +
            "You can only use this effect of \"Dragon Commander\" once per turn.", lines[0]);
        Assert.Equal(
            "During your opponent's turn (Quick Effect): You can banish this card from your GY, then target 1 monster your opponent controls; " +
            "destroy it, and if you do, inflict 500 damage to your opponent. You can only use this effect of \"Dragon Commander\" once per turn.", lines[1]);
        Assert.Equal("While you control 2 or more face-up Dragon monsters, Dragon monsters you control gain 300 ATK.", lines[2]);
    }

    [Fact]
    public void SummonedThisTurnQuickEffect_Reads()
    {
        var effect = new EffectEvent
        {
            When = new BlockInstance("when.quick"),
            If = [new BlockInstance("if.summoned_this_turn").With("how", "special")],
            Do = [new BlockInstance("do.draw")],
        };
        Assert.Equal("If this card was Special Summoned this turn (Quick Effect): You can draw 1 card.",
            EffectTextGenerator.Describe(effect, new CardCrafter.Core.Model.CardData { Name = "X" }));
    }

    [Fact]
    public void NegateAndLinkTemplates_ReadLikeCardText()
    {
        string Text(string template)
        {
            var t = CardCrafter.Core.Templates.EffectTemplates.All.Single(x => x.Name == template);
            return EffectTextGenerator.Generate(TemplateTests.CardWith(t));
        }
        Assert.Equal("When your opponent activates a card or effect (Quick Effect): You can discard this card; negate the activation, " +
                     "and if you do, destroy that card. You can only use this effect of \"Ash Blossom & Joyous Spring\" once per turn.",
            Text("Negate an activation"));
        Assert.Equal("(Quick Effect): You can discard this card, then target 1 face-up monster your opponent controls; " +
                     "negate its effects until the end of this turn.", Text("Negate a monster's effects"));
        // The Link monster's materials come first: the template card's procedure is the default "2 monsters".
        Assert.Equal("2 monsters\nMonsters this card points to gain 500 ATK, and monsters this card points to cannot be destroyed by battle.", Text("Link support"));
    }

    [Fact]
    public void SpellActivation_HasNoYouCan()
    {
        Assert.Equal("Draw 2 cards.", EffectTextGenerator.Generate(Samples.PotOfGreedClone()));
    }

    [Theory]
    [InlineData("draw 1 card", "draw 1 card")]
    [InlineData("Destroy 1 card", "destroy 1 card")]
    [InlineData("Dragon monsters you control", "Dragon monsters you control")]
    [InlineData("Special Summon this card", "Special Summon this card")]
    [InlineData("GY", "GY")]
    public void SentenceCase_KeepsCardTerms(string input, string expected) =>
        Assert.Equal(expected, SentenceCase.Lower(input));
}

public class SummonRuleTextTests
{
    private static CardProject With(string template, SummonRule rule)
    {
        var p = TemplateTests.CardWith(CardCrafter.Core.Templates.EffectTemplates.All.Single(t => t.Name == template));
        p.Card.Name = "Test Monster";
        p.Card.SummonRule = rule;
        return p;
    }

    [Fact]
    public void FreeSummon_ReadsLikeCyberDragon() =>
        Assert.Equal("If your opponent controls more monsters than you do, you can Special Summon this card (from your hand).",
            EffectTextGenerator.Generate(With("Free summon from hand", SummonRule.Normal)));

    [Fact]
    public void SemiNomi_SaysMustFirstBe() =>
        Assert.Equal("Cannot be Normal Summoned/Set. Must first be Special Summoned (from your hand) by banishing 2 monsters from your GY.",
            EffectTextGenerator.Generate(With("Banish from GY to summon", SummonRule.SpecialSummonOnly)));

    [Fact]
    public void Nomi_SaysCannotBeSpecialSummonedByOtherWays() =>
        Assert.Equal("Cannot be Normal Summoned/Set. Must be Special Summoned (from your hand) by Tributing 1 monster you control, " +
            "and cannot be Special Summoned by other ways. You can only Special Summon \"Test Monster\" once per turn this way.",
            EffectTextGenerator.Generate(With("Tribute to summon", SummonRule.ProcedureOnly)));

    [Fact]
    public void NoProcedure_StillSaysCannotBeNormalSummoned()
    {
        var p = new CardProject { Card = new CardData { Name = "Test Monster", SummonRule = SummonRule.SpecialSummonOnly } };
        Assert.Equal("Cannot be Normal Summoned/Set.", EffectTextGenerator.Generate(p));
    }

    [Fact]
    public void ExtraDeck_ProcedureOnly_SaysMustBeFusionSummoned()
    {
        var p = new CardProject { Card = new CardData { Name = "F", Frame = MonsterFrame.Fusion, SummonRule = SummonRule.ProcedureOnly } };
        Assert.Equal("Must be Fusion Summoned.", EffectTextGenerator.Generate(p));
        Assert.Contains("sc:SetValue(aux.fuslimit)", CardCrafter.Core.Compiler.ScriptCompiler.Compile(p).Lua);
    }
}
