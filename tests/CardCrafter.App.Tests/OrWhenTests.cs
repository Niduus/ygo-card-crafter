using CardCrafter.App.ViewModels;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.App.Tests;

/// <summary>"+ Or when...": the new WHEN starts where the first one is and picks the next event.</summary>
public class OrWhenTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    [Fact]
    public void SameBlock_MovesOnToTheNextEvent_AndKeepsOptional()
    {
        var first = B("when.summoned", ("how", "normal"), ("optional", "false"));
        var def = BlockRegistry.Find("when.summoned")!;
        var second = EffectViewModel.NextWhen(def, first, []);
        Assert.Equal("special", second.Values["how"]);
        Assert.Equal("false", second.Values["optional"]);
        var third = EffectViewModel.NextWhen(def, first, [second]);
        Assert.Equal("flip", third.Values["how"]);
    }

    [Fact]
    public void OtherBlock_KeepsWhereThisCardIs()
    {
        var first = B("when.event", ("event", "opp_normal"), ("zone", "hand"));
        var next = EffectViewModel.NextWhen(BlockRegistry.Find("when.event")!, first, []);
        Assert.Equal("opp_special", next.Values["event"]);
        Assert.Equal("hand", next.Values["zone"]);
        var cards = EffectViewModel.NextWhen(BlockRegistry.Find(CardsEventTrigger.BlockId)!, first, []);
        Assert.Equal("hand", cards.Values["zone"]);
    }

    [Fact]
    public void FromTheGy_IsOnlyShownForSpecialSummons()
    {
        var def = BlockRegistry.Find("when.summoned")!;
        var inst = B("when.summoned", ("how", "normal"));
        var from = new ChoiceSegmentViewModel(def.Param("from")!, inst, () => { });
        Assert.False(from.IsShown);
        inst.Values["how"] = "special";
        Assert.True(from.IsShown);
    }
}

public class MaterialOrTests
{
    [Fact]
    public void AFusionMaterialCanTakeAnotherKind()
    {
        var slot = new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, CardCode = 74677422 } };
        var changed = 0;
        var vm = new MaterialSlotViewModel(slot, () => new CardPack(), () => changed++, _ => { });
        Assert.False(vm.HasOr);
        vm.AddOrCommand.Execute(null);
        Assert.True(vm.HasOr);
        Assert.NotNull(slot.Or);
        slot.Or!.Race = CardRace.Dragon;
        slot.Or.MonsterKind = MonsterKind.Effect;
        Assert.Equal("or Dragon Effect Monster", vm.OrSummary);
        vm.RemoveOrCommand.Execute(null);
        Assert.Null(slot.Or);
        Assert.Equal(2, changed);
    }
}
