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
