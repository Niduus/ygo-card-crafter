using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

/// <summary>
/// "(This card is always treated as a "X" card.)": when it applies (the card's name doesn't say the archetype), where it is
/// printed, its plural form, the per-card switch, the note about typed text and its fix, and that a "This card is always
/// treated as" block never prints it a second time. The expected texts are official card texts.
/// </summary>
public class ArchetypeReminderTests
{
    private const int Hero = 0x8, ElementalHero = 0x3008, Kuriboh = 0xa4, PhantomBeast = 0x1b, Archfiend = 0x45, NobleArms = 0x107a, SynchroDragon = 0x2017;

    private static CardPack Pack(params CardProject[] cards) => new()
    {
        Cards = [.. cards],
        Archetypes =
        [
            new Archetype { Code = Hero, Name = "HERO" }, new Archetype { Code = ElementalHero, Name = "Elemental HERO" }, new Archetype { Code = Kuriboh, Name = "Kuriboh" },
            new Archetype { Code = PhantomBeast, Name = "Phantom Beast" }, new Archetype { Code = Archfiend, Name = "Archfiend" }, new Archetype { Code = NobleArms, Name = "Noble Arms" },
            new Archetype { Code = SynchroDragon, Name = "Synchro Dragon" },
        ],
    };

    private static CardProject Monster(string name, params int[] archetypes) => new()
    {
        Card = new CardData { Id = 100300001, Name = name, Level = 4, Archetypes = [.. archetypes] },
    };

    /// <summary>Official texts end some lines with a tab and use CRLF; compared line by line without them.</summary>
    private static string Lines(string text) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    // ---------------------------------------------------------------- when the name says it

    [Theory]
    // In the name, whatever the case or punctuation: no reminder on these official cards.
    [InlineData("Blue-Eyes White Dragon", "Blue-Eyes", true)]
    [InlineData("Elemental HERO Neos", "HERO", true)]
    [InlineData("Neos Wiseman", "Neos", true)]
    [InlineData("Ankuriboh", "Kuriboh", true)]
    [InlineData("Lord Gaia the Fierce Knight", "Gaia The Fierce Knight", true)]
    [InlineData("Counter Gem", "Gem-", true)]
    [InlineData("Raidraptor's Phantom Knights Claw", "Phantom Knights'", true)]
    // Not in the name: these official cards print the reminder.
    [InlineData("Favorite HERO Flame Wingman", "Elemental HERO", false)]
    [InlineData("Kuriboo", "Kuriboh", false)]
    [InlineData("Gazelle the King of Mythical Beasts", "Phantom Beast", false)]
    [InlineData("Junoldo the Shadespirit Power Patron", "Elfnote", false)]
    public void TheNameSaysTheArchetype_LikeOfficialCards(string name, string archetype, bool named) =>
        Assert.Equal(named, ArchetypeReminder.IsNamed(name, archetype));

    [Fact]
    public void OnlyTheArchetypesTheNameDoesntSay_AreListed()
    {
        // Favorite HERO Flame Wingman is a "HERO" by its name, and prints the reminder for "Elemental HERO".
        var p = Monster("Favorite HERO Flame Wingman", Hero, ElementalHero);
        Assert.Equal(["Elemental HERO"], ArchetypeReminder.Unnamed(p, Pack(p)));
        Assert.StartsWith("(This card is always treated as an \"Elemental HERO\" card.)", EffectTextGenerator.Generate(p, Pack(p)));
        var named = Monster("Elemental HERO Ember", Hero, ElementalHero);
        Assert.Equal("", EffectTextGenerator.Generate(named, Pack(named)));
    }

    // ---------------------------------------------------------------- the line, as official cards print it

    [Theory]
    [InlineData("(This card is always treated as a \"Magistus\" and \"Endymion\" card.)", "Magistus", "Endymion")]        // Magia Magic - Thunder of Judgment
    [InlineData("(This card is always treated as an \"Ursarctic\" and \"Drytron\" card.)", "Ursarctic", "Drytron")]        // Ultimate Bright Knight Ursatron Alpha
    [InlineData("(This card is always treated as a \"Zubaba\", \"Gagaga\", \"Gogogo\", and \"Dododo\" card.)", "Zubaba", "Gagaga", "Gogogo", "Dododo")] // Utopic Onomatopoeia
    [InlineData("(This card is always treated as a \"Utopic Future\" card.)", "Utopic Future")]                           // Number F0: Utopic Draco Future
    [InlineData("(This card is always treated as an \"X-Saber\" card.)", "X-Saber")]                                     // Commander Souza, Swordmaster
    [InlineData("(This card is always treated as a \"F.A.\" card.)", "F.A.")]                                            // U.A. Hyper Stadium
    public void TheLine_ReadsLikeOfficialCards(string official, params string[] names) =>
        Assert.Equal(official, ArchetypeReminder.Line(names));

    [Fact]
    public void ASpell_PrintsItFirst_OnItsOwnLine()
    {
        // Celestial Sword - Eatos: "(This card is always treated as a "Noble Arms" card.)\r\nThe equipped monster gains 500 ATK. When ..."
        const string official = "(This card is always treated as a \"Noble Arms\" card.)\t\r\nThe equipped monster gains 500 ATK. When this card is sent from the field to the GY: You can target 1 \"Guardian Eatos\" you control; it gains 500 ATK for each banished monster.";
        var p = new CardProject
        {
            Card = new CardData { Id = 100300010, Name = "Celestial Sword - Eatos", Kind = CardKind.Spell, SpellKind = SpellKind.Equip, Archetypes = [NobleArms] },
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.equipped", ("rule", "gain_atk"), ("amount", "500"))] }],
        };
        var text = EffectTextGenerator.Generate(p, Pack(p));
        Assert.Equal("(This card is always treated as a \"Noble Arms\" card.)\nThe equipped monster gains 500 ATK.", text);
        Assert.StartsWith(text, Lines(official));
    }

    [Fact]
    public void AnExtraDeckMonster_PrintsItAfterItsMaterials()
    {
        // Chimera the Flying Mythical Beast.
        const string official = "\"Gazelle the King of Mythical Beasts\" + \"Berfomet\"\t\r\n(This card is always treated as a \"Phantom Beast\" card.)\r\nWhen this card is destroyed: You can target 1 \"Berfomet\" or 1 \"Gazelle the King of Mythical Beasts\" in your GY; Special Summon that target.";
        var gazelle = new CardProject { Card = new CardData { Id = 5818798, Name = "Gazelle the King of Mythical Beasts", Frame = MonsterFrame.Normal } };
        var berfomet = new CardProject { Card = new CardData { Id = 77207191, Name = "Berfomet" } };
        var chimera = new CardProject
        {
            Card = new CardData { Id = 100300011, Name = "Chimera the Flying Mythical Beast", Frame = MonsterFrame.Fusion, Level = 6, Archetypes = [PhantomBeast] },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [5818798, 77207191] },
        };
        var text = EffectTextGenerator.Generate(chimera, Pack(chimera, gazelle, berfomet));
        Assert.Equal(string.Join("\n", Lines(official).Split('\n').Take(2)), text);
    }

    [Theory]
    [InlineData("Gazelle the King of Mythical Beasts", PhantomBeast, "This monster moves so fast that it looks like an illusion to mortal eyes.\t\r\n(This card is always treated as a \"Phantom Beast\" card.)")]
    [InlineData("Beast of Talwar", Archfiend, "Only the master of the sword among Fiend-Type monsters is permitted to hold the Talwar.\t\r\n(This card is always treated as an \"Archfiend\" card.)")]
    [InlineData("Summoned Skull", Archfiend, "A fiend with dark powers for confusing the enemy. Among the Fiend-Type monsters, this monster boasts considerable force.\t\r\n(This card is always treated as an \"Archfiend\" card.)")]
    public void ANormalMonster_GetsItAfterItsFlavorText(string name, int archetype, string official)
    {
        var p = Monster(name, archetype);
        p.Card.Frame = MonsterFrame.Normal;
        p.Card.Text = Lines(official).Split('\n')[0];
        var pack = Pack(p);
        Assert.NotNull(ArchetypeReminder.Check(p, pack));
        Assert.Equal(Lines(official), ArchetypeReminder.AddTo(p, pack));
        p.Card.Text = ArchetypeReminder.AddTo(p, pack);
        Assert.Null(ArchetypeReminder.Check(p, pack));
    }

    [Fact]
    public void APendulumMonster_HasItInTheMonsterEffectBox()
    {
        // White Wing Magician: "[ Monster Effect ]\r\n(This card is always treated as a "Synchro Dragon" card.)\t\r\nIf this Pendulum Summoned card ..."
        var p = Monster("White Wing Magician", SynchroDragon);
        p.Card.Abilities = MonsterAbility.Pendulum;
        p.Card.LeftScale = p.Card.RightScale = 1;
        p.Effects.Add(new EffectEvent { When = B("when.ignition", ("zone", "pendulum_zone")), Do = [B("do.draw")] });
        var pack = Pack(p);
        Assert.DoesNotContain("always treated", EffectTextGenerator.GeneratePendulum(p, pack));
        Assert.Equal("(This card is always treated as a \"Synchro Dragon\" card.)", EffectTextGenerator.Generate(p, pack));
        Assert.Contains("[ Monster Effect ]\n(This card is always treated as a \"Synchro Dragon\" card.)", EffectTextGenerator.GenerateWithSections(p, pack));
    }

    [Fact]
    public void ItComesBeforeTheSummoningConditions()
    {
        // Ultimate Bright Knight Ursatron Alpha: "(This card is always treated as an "Ursarctic" and "Drytron" card.)\r\nCannot be Normal Summoned/Set. ..."
        var p = Monster("Ultimate Bright Knight Ursatron Alpha", PhantomBeast, Archfiend);
        p.Card.SummonRule = SummonRule.SpecialSummonOnly;
        Assert.Equal("(This card is always treated as a \"Phantom Beast\" and \"Archfiend\" card.)\nCannot be Normal Summoned/Set.", EffectTextGenerator.Generate(p, Pack(p)));
    }

    // ---------------------------------------------------------------- the switch, and older projects

    [Fact]
    public void TheCardCanTurnItOff()
    {
        var p = Monster("Kuriboo", Kuriboh);
        Assert.StartsWith("(This card is always treated as a \"Kuriboh\" card.)", EffectTextGenerator.Generate(p, Pack(p)));
        p.Card.ArchetypeReminder = false;
        Assert.Equal("", EffectTextGenerator.Generate(p, Pack(p)));
        p.Card.Text = "When an opponent's monster declares an attack: You can discard this card.";
        Assert.Null(ArchetypeReminder.Check(p, Pack(p)));
        // Saved and opened again.
        var back = PackSerializer.FromJson(PackSerializer.ToJson(Pack(p))).Cards[0];
        Assert.False(back.Card.ArchetypeReminder);
    }

    [Fact]
    public void OlderProjects_LoadWithItOn()
    {
        var json = PackSerializer.ToJson(Pack(Monster("Kuriboo", Kuriboh))).Replace("\"archetypeReminder\": true,", "", StringComparison.Ordinal);
        Assert.DoesNotContain("archetypeReminder", json);
        Assert.True(PackSerializer.FromJson(json).Cards[0].Card.ArchetypeReminder);
    }

    // ---------------------------------------------------------------- typed text: the note and its fix

    [Fact]
    public void TypedText_WithoutIt_GetsANote_AndTheFixPutsItFirst()
    {
        var p = Monster("Kuriboo", Kuriboh);
        p.Card.Text = "When an opponent's monster declares an attack: You can discard this card.";
        var pack = Pack(p);
        var note = ArchetypeReminder.Check(p, pack);
        Assert.NotNull(note);
        Assert.Equal(Severity.Info, note!.Severity);
        Assert.Contains("(This card is always treated as a \"Kuriboh\" card.)", note.Message);
        Assert.Contains(ScriptCompiler.Compile(p, pack).Diagnostics, d => d.Message == note.Message);
        Assert.Equal("(This card is always treated as a \"Kuriboh\" card.)\nWhen an opponent's monster declares an attack: You can discard this card.", ArchetypeReminder.AddTo(p, pack));
        // Empty text gets the suggested text, which has it: no note.
        p.Card.Text = "";
        Assert.Null(ArchetypeReminder.Check(p, pack));
    }

    [Fact]
    public void TheFix_CompletesAReminderThatMissesAnArchetype_AndKeepsCrLf()
    {
        var p = Monster("Ember Knight", Kuriboh, Archfiend);
        p.Card.Text = "(This card is always treated as a \"Kuriboh\" card.)\r\nThis card gains 100 ATK.";
        var pack = Pack(p);
        Assert.NotNull(ArchetypeReminder.Check(p, pack));
        Assert.Equal("(This card is always treated as a \"Kuriboh\" and \"Archfiend\" card.)\r\nThis card gains 100 ATK.", ArchetypeReminder.AddTo(p, pack));
        // An Extra Deck monster's typed text: after the materials.
        var x = Monster("Abyss Rider", Archfiend);
        x.Card.Frame = MonsterFrame.Xyz;
        x.Card.Text = "2 Level 4 monsters\nThis card gains 100 ATK.";
        Assert.Equal("2 Level 4 monsters\n(This card is always treated as an \"Archfiend\" card.)\nThis card gains 100 ATK.", ArchetypeReminder.AddTo(x, Pack(x)));
    }

    // ---------------------------------------------------------------- never twice

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithAnAlwaysTreatedAsBlock_ItIsPrintedOnce(bool reminder)
    {
        var p = Monster("Ember Knight", Kuriboh);
        p.Card.ArchetypeReminder = reminder;
        p.Effects.Add(new EffectEvent { When = B("when.while_face_up"), Do = [B(TreatedAsPassive.BlockId).With("as", new CardFilter { CardType = FilterCardType.Any, Archetype = Archfiend })] });
        p.Effects.Add(new EffectEvent { When = B("when.ignition"), Do = [B("do.draw")] });
        var text = EffectTextGenerator.Generate(p, Pack(p));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "always treated as"));
        Assert.Equal(reminder
            ? "(This card is always treated as a \"Kuriboh\" and \"Archfiend\" card.)\nYou can draw 1 card."
            : "(This card is always treated as an \"Archfiend\" card.) You can draw 1 card.", text);
        // Both archetypes are in the database entry either way.
        Assert.Equal([Kuriboh, Archfiend], TreatedAsPassive.Setcodes(p));
    }

    [Fact]
    public void AnAlwaysTreatedAsArchetypeTheNameSays_IsNotReminded()
    {
        var p = Monster("Archfiend Ember", Kuriboh);
        p.Effects.Add(new EffectEvent { When = B("when.while_face_up"), Do = [B(TreatedAsPassive.BlockId).With("as", new CardFilter { CardType = FilterCardType.Any, Archetype = Archfiend })] });
        Assert.Equal("(This card is always treated as a \"Kuriboh\" card.)", EffectTextGenerator.Generate(p, Pack(p)));
    }

    [Fact]
    public void TheExport_WritesItOnce_AndTheSetcodes()
    {
        var p = Monster("Ember Knight", Kuriboh);
        p.Effects.Add(new EffectEvent { When = B("when.while_face_up"), Do = [B(TreatedAsPassive.BlockId).With("as", new CardFilter { CardType = FilterCardType.Any, Archetype = Archfiend })] });
        using var tmp = new TempDir();
        var result = PackExporter.Export(Pack(p), tmp.Path);
        var row = CdbFile.Read(result.DatabasePath).Single();
        Assert.Equal("(This card is always treated as a \"Kuriboh\" and \"Archfiend\" card.)", row.Desc);
        Assert.Equal(CdbEncoder.PackSetcodes([Kuriboh, Archfiend]), row.Setcode);
    }
}
