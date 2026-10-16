using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

/// <summary>The card browser's colours (green, yellow, orange, red) and the card text each effect quotes.</summary>
public class VerdictTests
{
    private static ScriptPiece Piece(string title = "Effect", string code = "") => new() { Title = title, Code = code, Functions = [], Prefix = "s", SourceId = 1 };

    [Fact]
    public void Colours_FollowWhatBecameOfThePiece()
    {
        var card = new CardData { Id = 1, Name = "X", Level = 4 };
        var green = new TranslatedPiece { Piece = Piece(), Effect = new EffectEvent { When = new BlockInstance("when.ignition"), Do = [new BlockInstance("do.draw")] } };
        Assert.Equal(PieceColor.Green, TranslationVerdict.Of(green, card).Color);
        Assert.Equal(PieceColor.Yellow, TranslationVerdict.Of(new TranslatedPiece { Piece = Piece(), Ignored = true }, card).Color);
        Assert.Equal(PieceColor.Yellow, TranslationVerdict.Of(new TranslatedPiece { Piece = Piece(), Rule = SummonRule.ProcedureOnly }, card).Color);
        var orange = TranslationVerdict.Of(new TranslatedPiece { Piece = Piece(), Reason = "its target step does something the blocks don't (local b1=...)" }, card);
        Assert.Equal(PieceColor.Orange, orange.Color);
        Assert.Contains("built by hand", orange.Note);
        var red = TranslationVerdict.Of(new TranslatedPiece { Piece = Piece(), Reason = "its lasting effect isn't one the blocks have (FIELD EFFECT_CHANGE_BATTLE_DAMAGE)" }, card);
        Assert.Equal(PieceColor.Red, red.Color);
        Assert.Contains("EFFECT_CHANGE_BATTLE_DAMAGE", red.Note);
        // A lasting effect some block writes, only in another form, can be built by hand.
        Assert.Equal(PieceColor.Orange, TranslationVerdict.Of(new TranslatedPiece { Piece = Piece(), Reason = "its lasting effect isn't one the blocks have (SINGLE EFFECT_UPDATE_ATTACK)" }, card).Color);
        Assert.Equal(PieceColor.Red, TranslationVerdict.Of(new TranslatedPiece { Piece = Piece(), Reason = "its lasting effect isn't one the blocks have (SINGLE+CONTINUOUS EVENT_SPSUMMON_SUCCESS)" }, card).Color);
    }

    [Fact]
    public void Sentences_KeepBulletsWithTheirSentence_AndQuotedNamesWhole()
    {
        var s = EffectCitation.Sentences("Once per turn: You can activate 1 of these effects;\r\n● Draw 1 card.\r\n● Gain 500 LP.\r\nAdd 1 \"Mr. Volcano\" from your Deck to your hand. You can only use each effect of \"X\" once per turn.");
        Assert.Equal(2, s.Count);
        Assert.Equal("Once per turn: You can activate 1 of these effects; ● Draw 1 card. ● Gain 500 LP.", s[0]);
        Assert.Equal("Add 1 \"Mr. Volcano\" from your Deck to your hand.", s[1]);
    }

    [Fact]
    public void EachPiece_QuotesItsOwnSentence()
    {
        var card = new CardData { Id = 1, Name = "X", Level = 4 };
        var text = "If this card is Normal Summoned: You can draw 1 card. If this card is destroyed: Inflict 500 damage to your opponent.";
        var pieces = new List<TranslatedPiece>
        {
            new() { Piece = Piece("Damage", "e1:SetCategory(CATEGORY_DAMAGE)\ne1:SetCode(EVENT_DESTROYED)"), Reason = "x" },
            new() { Piece = Piece("Draw", "e2:SetCategory(CATEGORY_DRAW)\ne2:SetCode(EVENT_SUMMON_SUCCESS)"), Reason = "x" },
        };
        var cites = EffectCitation.Assign(text, pieces, card);
        Assert.Equal("If this card is destroyed: Inflict 500 damage to your opponent.", cites[0]);
        Assert.Equal("If this card is Normal Summoned: You can draw 1 card.", cites[1]);
    }
}
