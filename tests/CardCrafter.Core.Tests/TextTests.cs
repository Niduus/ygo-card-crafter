using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

public class TextTests
{
    [Fact]
    public void DragonCommander_ReadsLikeCardText()
    {
        var p = Samples.DragonCommander();
        var lines = EffectTextGenerator.Generate(p, Samples.Pack(p)).Split('\n');
        Assert.Equal(
            "If this card is Normal or Special Summoned: You can add 1 Level 4 or lower Dragon monster from your Deck to your hand. " +
            "You can only use this effect of \"Dragon Commander\" once per turn.", lines[0]);
        Assert.Equal(
            "During your opponent's turn (Quick Effect): You can banish this card, then target 1 monster your opponent controls; " +
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
        Assert.Equal("Monsters this card points to gain 500 ATK, and monsters this card points to cannot be destroyed by battle.", Text("Link support"));
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
