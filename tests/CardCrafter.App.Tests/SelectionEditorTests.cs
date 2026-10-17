using CardCrafter.App.ViewModels;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.App.Tests;

/// <summary>The selection blanks on the event sheet: shown only while they apply, and "●" options' own costs offered.</summary>
public class SelectionEditorTests
{
    private static (CardViewModel Card, CardProject Project) Card(params BlockInstance[] dos)
    {
        var project = new CardProject
        {
            Card = new CardData { Id = 100112001, Name = "Editor", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = BlockRegistry.Find("when.activate")!.CreateInstance(), Do = [.. dos] }],
        };
        var pack = new CardPack();
        return (new CardViewModel(project, () => pack, () => { }), project);
    }

    private static SegmentViewModel Segment(BlockViewModel block, string label) =>
        block.Segments.First(s => s switch
        {
            ChoiceSegmentViewModel c => c.Label == label,
            IntSegmentViewModel i => i.Label == label,
            BoolSegmentViewModel b => b.Label == label,
            CardsSegmentViewModel k => k.Label == label,
            _ => false,
        });

    private static bool Shown(SegmentViewModel s) => s switch
    {
        ChoiceSegmentViewModel c => c.IsShown,
        IntSegmentViewModel i => i.IsShown,
        BoolSegmentViewModel b => b.IsShown,
        CardsSegmentViewModel k => k.IsShown,
        _ => true,
    };

    [Fact]
    public void GroupBlanks_AreShownForSeveralCards_AndTheCountForTheNumberGiven()
    {
        var destroy = BlockRegistry.Find("do.destroy")!.CreateInstance();
        var (card, _) = Card(destroy);
        var block = card.Effects[0].Actions[0];
        // One card: no group rule, no "1 each".
        Assert.False(Shown(Segment(block, "The cards as a group")));
        Assert.False(Shown(Segment(block, "1 each (from each place, or from each player)")));
        Assert.True(Shown(Segment(block, "Who picks")));
        destroy.Values["count"] = "2";
        Assert.True(Shown(Segment(block, "The cards as a group")));
        // "up to the number of ...": the count is counted, from the cards picked here.
        destroy.Values["amount"] = "number_of";
        Assert.False(Shown(Segment(block, "How many")));
        Assert.True(Shown(Segment(block, "The number of")));
        destroy.Values["amount"] = "count";
        Assert.False(Shown(Segment(block, "The number of")));
    }

    [Fact]
    public void TheCountedCards_AreOnlySavedOnceEdited()
    {
        var destroy = BlockRegistry.Find("do.destroy")!.CreateInstance();
        Assert.False(destroy.Filters.ContainsKey("count_cards"));
        var (card, _) = Card(destroy);
        var counted = (CardsSegmentViewModel)Segment(card.Effects[0].Actions[0], "The number of");
        Assert.False(destroy.Filters.ContainsKey("count_cards"));
        destroy.Values["amount"] = "number_of";
        Assert.Equal("face-up monsters you control", counted.Summary);
        counted.Editor.Race = counted.Editor.RaceOptions.First(o => o.Value == CardRace.Dragon);
        Assert.True(destroy.Filters.ContainsKey("count_cards"));
    }

    [Fact]
    public void OptionCosts_AreOfferedUnderAChooseBlock()
    {
        var (card, project) = Card(BlockRegistry.Find("do.draw")!.CreateInstance());
        var effect = card.Effects[0];
        Assert.Empty(effect.AvailableOptionCosts);
        project.Effects[0].Do.Insert(0, BlockRegistry.Find(ChooseEffectsAction.BlockId)!.CreateInstance());
        Assert.NotEmpty(effect.AvailableOptionCosts);
        effect.AddGate(BlockRegistry.Find("cost.discard")!);
        Assert.Equal("cost.discard", project.Effects[0].Do[^1].BlockId);
        Assert.Equal("COST", effect.Actions[^1].KindLabel);
    }

    [Fact]
    public void ChooseBlock_HidesHowManyWhenTheOpponentChooses()
    {
        var header = BlockRegistry.Find(ChooseEffectsAction.BlockId)!.CreateInstance();
        var (card, _) = Card(header, BlockRegistry.Find("do.draw")!.CreateInstance());
        var block = card.Effects[0].Actions[0];
        Assert.True(Shown(Segment(block, "How many")));
        header.Values["who"] = "opponent";
        Assert.False(Shown(Segment(block, "How many")));
        Assert.False(Shown(Segment(block, "When")));
    }
}
