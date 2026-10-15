using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Red-Eyes Dark Dragoon, translated from its official script into blocks and played.</summary>
public class DragoonTests
{
    private const long DragoonId = 37818794, DarkMagician = 46986414, RedEyes = 74677422;

    private static string? Script() =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{DragoonId}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{DragoonId}.lua")) : null;

    private static CardProject Translated()
    {
        var card = new CardData { Id = DragoonId, Name = "Red-Eyes Dark Dragoon", Frame = MonsterFrame.Fusion, Level = 8, Atk = 3000, Def = 2500, Race = CardRace.Dragon, Attribute = CardAttribute.Dark };
        var project = new CardProject { Card = card, UseCustomLua = true, CustomLua = Script()! };
        ScriptTranslator.ApplyTo(project, null, out var applied);
        Assert.True(applied, string.Join("\n", ScriptTranslator.Untranslated(ScriptTranslator.Translate(project.CustomLua, DragoonId, card))));
        return project;
    }

    [EngineFact]
    public void TranslatesCompletely_AndReadsLikeTheCard()
    {
        if (Script() is null) return;
        var p = Translated();
        CardNames.Set([(DarkMagician, "Dark Magician"), (RedEyes, "Red-Eyes Black Dragon")]);
        try
        {
            var text = EffectTextGenerator.Generate(p);
            Assert.StartsWith("\"Dark Magician\" + \"Red-Eyes Black Dragon\" or 1 Dragon Effect Monster\n", text);
            Assert.Contains("This card cannot be targeted by card effects.", text);
            Assert.Contains("inflict damage to your opponent equal to that monster's original ATK. You can use this effect a number of times per turn up to the number of Normal Monsters used as material for this card.", text);
            Assert.Contains("negate the activation, and if you do, destroy that card, and if you do, this card gains 1000 ATK.", text);
        }
        finally { CardNames.Set([]); }
    }

    [EngineFact]
    public void FusionSummonedWithTwoNormalMonsters_DestroysAndBurnsTwice()
    {
        if (Script() is null) return;
        var dragoon = Translated();
        var poly = new CardProject
        {
            Card = new CardData { Id = 100070001, Name = "Fuse", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = BlockRegistry.Find("when.activate")!.CreateInstance(), Do = [BlockRegistry.Find(FusionSummonAction.BlockId)!.CreateInstance()] }],
        };
        var dm = new CardProject { Card = new CardData { Id = DarkMagician, Name = "Dark Magician", Frame = MonsterFrame.Normal, Level = 7, Atk = 2500, Def = 2100, Race = CardRace.Spellcaster, Attribute = CardAttribute.Dark } };
        var reb = new CardProject { Card = new CardData { Id = RedEyes, Name = "Red-Eyes Black Dragon", Frame = MonsterFrame.Normal, Level = 7, Atk = 2400, Def = 2000, Race = CardRace.Dragon, Attribute = CardAttribute.Dark } };
        new EngineScenario(dragoon, poly, dm, reb)
            .Card(poly.Card.Id, 0, "hand").Card(DarkMagician, 0, "hand").Card(RedEyes, 0, "hand").Card(DragoonId, 0, "extra")
            .Card(Warrior, 1, "mzone", 3).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(poly.Card.Id).Activate(DragoonId, 5)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{DragoonId})")
            // Two Normal Monsters as material: two uses, each burning a Warrior's 1500 original ATK.
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1")
            .Check("Duel.GetLP(1)==5000").AssertPasses();
    }
}
