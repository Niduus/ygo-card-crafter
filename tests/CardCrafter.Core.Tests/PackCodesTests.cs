using CardCrafter.Core.Model;

namespace CardCrafter.Core.Tests;

public class PackCodesTests
{
    [Fact]
    public void RandomArchetypeCodeAvoidsTakenArchetypes()
    {
        var taken = Enumerable.Range(0x100, 0xf00).Where(c => c != 0x777).Select(c => c | 0x1000).ToList();
        Assert.Equal(0x777, PackCodes.RandomArchetypeCode(taken));
        Assert.Equal(0, PackCodes.RandomArchetypeCode(taken.Append(0x777)));
        var seen = Enumerable.Range(0, 50).Select(i => PackCodes.RandomArchetypeCode([], new Random(i))).ToHashSet();
        Assert.All(seen, c => Assert.InRange(c, 0x100, 0xfff));
        Assert.True(seen.Count > 10);
    }

    [Fact]
    public void RandomTypeValueIsAFreeBitOutsideTheStandardTypes()
    {
        for (var i = 0; i < 30; i++)
        {
            var v = PackCodes.RandomTypeValue([0x8000000], new Random(i));
            Assert.Equal(0, v & (v - 1));
            Assert.Equal(0, v & EnumNames.StandardRaceMask);
            Assert.NotEqual(0x8000000, v);
            Assert.True(v <= 0x80000000L);
        }
    }

    [Fact]
    public void ChangingACodeMovesCardsAndFilters()
    {
        var card = new CardProject { Card = new CardData { Id = 100000001, Name = "A", Archetypes = [0xa01], Race = (CardRace)0x4000000 } };
        var search = new BlockInstance("do.search");
        search.Filters["cards"] = new CardFilter { Archetype = 0xa01, Race = (CardRace)(0x4000000 | 0x1) };
        card.Effects.Add(new EffectEvent { When = new BlockInstance("when.ignition"), Do = [search] });
        card.Procedure.Materials.Add(new MaterialSlot { Or = new CardFilter { Archetype2 = 0xa01 } });
        var pack = new CardPack { Cards = [card] };

        Assert.Equal(3, PackCodes.ChangeArchetype(pack, 0xa01, 0xb22));
        Assert.Equal([0xb22], card.Card.Archetypes);
        Assert.Equal(0xb22, search.Filters["cards"].Archetype);
        Assert.Equal(0xb22, card.Procedure.Materials[0].Or!.Archetype2);

        Assert.Equal(2, PackCodes.ChangeType(pack, 0x4000000, 0x10000000));
        Assert.Equal((CardRace)0x10000000, card.Card.Race);
        Assert.Equal((CardRace)(0x10000000 | 0x1), search.Filters["cards"].Race);
    }

    [Fact]
    public void ATypeValueWithStandardBits_IsFlagged()
    {
        // "Infested" at 0x100000230 shows in the game as Zombie, Machine and Winged-Beast (0x10 + 0x20 + 0x200).
        var why = PackCodes.TypeValueProblem(0x100000230);
        Assert.NotNull(why);
        Assert.Contains("Zombie", why);
        Assert.Contains("Machine", why);
        Assert.Contains("0x230", why);
        Assert.Contains("0x100000000", why);
        Assert.Null(PackCodes.TypeValueProblem(0x100000000));
        Assert.Null(PackCodes.TypeValueProblem(0x4000000));
    }
}
