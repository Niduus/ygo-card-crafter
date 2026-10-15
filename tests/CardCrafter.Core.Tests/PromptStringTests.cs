using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The database's str1..str16 behind every aux.Stringid a script uses: effect descriptions and "you can" prompts get
/// the effect's text in every Omega layout, and strings typed over the generated ones win.
/// </summary>
public class PromptStringTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    /// <summary>A hand monster with a trigger, a Quick Effect and a "you can" step.</summary>
    private static CardProject HandMonster() => new()
    {
        Card = new CardData { Id = 100020001, Name = "Prompt Tester", Kind = CardKind.Monster, Frame = MonsterFrame.Effect, Level = 4 },
        Effects =
        [
            new EffectEvent { When = B("when.cards_event", ("event", "summoned"), ("zone", "hand")), Do = [B("do.special_summon_self"), B("do.draw").MayDo().Linked(ActionLink.Then)] },
            new EffectEvent { When = B("when.quick", ("zone", "hand")), Do = [B("do.special_summon_self")] },
            new EffectEvent { When = B("when.summoned"), Do = [B("do.draw").MayDo()] },
        ],
    };

    [Theory]
    [InlineData(OmegaFormatTests.OldOmegaLayout)]
    [InlineData(OmegaFormatTests.OmegaLayout)]
    public void EveryStringidTheScriptUses_HasItsEffectTextInTheDatabase(string layout)
    {
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "Databases");
        Directory.CreateDirectory(dir);
        var db = OmegaFormatTests.MakeDb(Path.Combine(dir, "Mine.db"), layout);
        var project = HandMonster();
        var pack = new CardPack { Name = "Mine", Cards = [project] };

        var save = DatabasePack.Save(pack, db, null, Path.Combine(tmp.Path, "backups"));
        Assert.Null(save.Error);
        var lua = File.ReadAllText(Path.Combine(tmp.Path, "Scripts", $"c{project.Card.Id}.lua"));
        var row = CdbFile.Read(db).Single(r => r.Id == project.Card.Id);

        var used = Regex.Matches(lua, @"aux\.Stringid\(id,(\d+)\)").Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
        Assert.Equal([0, 1, 2], used.Order());
        foreach (var i in used) Assert.False(string.IsNullOrWhiteSpace(row.Strings[i]), $"str{i + 1} is empty");
        Assert.StartsWith("If a monster(s) is Normal or Special Summoned to your field: You can Special Summon this card from your hand, then you can draw 1 card.", row.Strings[0]);
        Assert.Contains("You can Special Summon this card from your hand.", row.Strings[1]);

        // Opening and saving again keeps them.
        var reopened = DatabasePack.Open(db);
        Assert.Null(DatabasePack.Save(reopened.Pack, db, null, Path.Combine(tmp.Path, "backups")).Error);
        Assert.Equal(row.Strings, CdbFile.Read(db).Single(r => r.Id == project.Card.Id).Strings);
    }

    [Fact]
    public void TypedStrings_ReplaceTheGeneratedOnes_AndEmptyKeepsThem()
    {
        var project = HandMonster();
        project.StringOverrides = ["", "Summon me now?"];
        var slots = ScriptCompiler.StringSlots(project);
        Assert.Equal(3, slots.Count);
        Assert.Equal("Effect 2", slots[1].Use);
        Assert.True(slots[1].Overridden);
        Assert.False(slots[0].Overridden);
        var strings = ScriptCompiler.Compile(project).Strings;
        Assert.Equal("Summon me now?", strings[1]);
        Assert.Equal(slots[0].Generated, strings[0]);
        Assert.Contains("You can draw 1 card.", strings[2]);
    }

    [Fact]
    public void ActivateOneOfTheseOptions_GetTheirOwnStrings()
    {
        var project = HandMonster();
        project.Effects[2].Do = [B("do.draw"), B("do.draw", ("count", "2")).Linked(ActionLink.Option)];
        var slots = ScriptCompiler.StringSlots(project);
        Assert.Equal(["Effect 1", "Effect 2", "Effect 3", "Effect 3, option 1", "Effect 3, option 2"], slots.Select(s => s.Use));
    }
}
