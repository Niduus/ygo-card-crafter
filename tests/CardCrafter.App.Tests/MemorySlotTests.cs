using CardCrafter.App.ViewModels;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.App.Tests;

/// <summary>The Memory blank offers the slots as the card uses them: what its other blocks remember in each.</summary>
public class MemorySlotTests
{
    [Fact]
    public void TheMemoryBlank_SaysWhatTheCardRemembersInEachSlot()
    {
        var project = new CardProject
        {
            Card = new CardData { Id = 100099790, Name = "Rememberer", Frame = MonsterFrame.Effect, Level = 4 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.ignition"),
                    Do = [BlockRegistry.Find("do.destroy")!.CreateInstance(), BlockRegistry.Find(RememberAction.BlockId)!.CreateInstance().With("what", "affected")],
                },
            ],
        };
        var reader = BlockRegistry.Find(MemoryCondition.BlockId)!;
        var inst = reader.CreateInstance();
        var slot = new ChoiceSegmentViewModel(reader.Param(Memory.SlotKey)!, inst, () => { }, () => project);
        Assert.Equal(["A", "B", "C", "D", "E"], slot.Choices.Select(c => c.Value));
        Assert.Equal("Memory A: the destroyed card until the end of this turn (effect 1)", slot.Choices[0].Display);
        Assert.Equal("Memory B (nothing remembered in it yet)", slot.Choices[1].Display);
        Assert.Equal("A", slot.Selected!.Value);
        // Without the card, the plain slots.
        Assert.Equal("Memory A", new ChoiceSegmentViewModel(reader.Param(Memory.SlotKey)!, inst, () => { }).Choices[0].Display);
    }
}
