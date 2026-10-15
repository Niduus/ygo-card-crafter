using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.Core.Tests;

public class BanlistTests
{
    private static PackBanlist List() => new()
    {
        Id = PackBanlist.FirstId, Name = "My Format",
        Entries =
        [
            new BanlistEntry { Code = 55144522, Limit = 0, Name = "Pot of Greed" },
            new BanlistEntry { Code = 210000001, Limit = 1, Name = "My Custom" },
        ],
    };

    [Fact]
    public void Banlists_AreWrittenIntoOmegasTables_AndReadBack()
    {
        using var tmp = new TempDir();
        var official = OmegaFormatTests.MakeDb(Path.Combine(tmp.Path, "official.db"), OmegaFormatTests.OmegaLayout);
        var schema = CdbSchema.Read(official)!;
        var pack = new CardPack { Name = "Banlist Pack", Banlists = [List()] };
        pack.Cards.Add(new CardProject { Card = new CardData { Id = 210000001, Name = "My Custom", Text = "x", Limitation = Limitation.SemiLimited } });
        var export = PackExporter.Export(pack, Path.Combine(tmp.Path, "out"), null, schema);

        var lists = CdbFile.ReadBanlists(export.DatabasePath);
        var mine = lists.Single(l => l.Id == PackBanlist.FirstId);
        Assert.Equal("My Format", mine.Name);
        Assert.Equal([(55144522L, 0), (210000001L, 1)], mine.Entries.Select(e => (e.Code, e.Limit)));
        // The card's own limitation is still on every one of the game's lists.
        Assert.All(lists.Where(l => l.Id < PackBanlist.FirstId), l => Assert.Equal(2, l.Entries.Single(e => e.Code == 210000001).Limit));
        Assert.Equal("My Custom", mine.Entries.Single(e => e.Code == 210000001).Name);

        var conf = File.ReadAllText(Path.Combine(tmp.Path, "out", "lflist.conf"));
        Assert.Contains("!My Format", conf);
        Assert.Contains("55144522 0 --Pot of Greed", conf);
    }

    [Fact]
    public void ADatabaseWithoutBanlistTables_GetsOmegasLayout()
    {
        using var tmp = new TempDir();
        var pack = new CardPack { Name = "Plain", Banlists = [List()] };
        pack.Cards.Add(new CardProject { Card = new CardData { Id = 210000001, Name = "My Custom", Text = "x" } });
        var export = PackExporter.Export(pack, tmp.Path);
        var list = Assert.Single(CdbFile.ReadBanlists(export.DatabasePath));
        Assert.Equal(2, list.Entries.Count);
    }

    [Fact]
    public void BanlistsSurviveSavingAPack()
    {
        var pack = new CardPack { Banlists = [List()] };
        var back = PackSerializer.FromJson(PackSerializer.ToJson(pack));
        Assert.Equal("My Format", back.Banlists.Single().Name);
        Assert.Equal(2, back.Banlists.Single().Entries.Count);
    }
}
