using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official shapes read back into the new blocks: the Crystal Beasts' placement instead of the GY, Heart of the
/// Blue-Eyes' fixed control, Herald of the Arc Light's banishment, Qliphort Genius' immunity, Exodia Incarnate's LP loss
/// and a Mimighoul's Special Summon to the opponent's field.
/// </summary>
public class MonsterCardTranslationTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScripts!, $"c{id}.lua"));

    private static List<EffectEvent> Translate(long id, CardData card) =>
        ScriptTranslator.Translate(Script(id), id, card).Pieces.SelectMany(p => p.Effects).ToList();

    private static CardData Monster(long id, string name, MonsterFrame frame = MonsterFrame.Effect) => new() { Id = id, Name = name, Frame = frame, Level = 4 };

    private static BlockInstance? Find(IEnumerable<EffectEvent> effects, string blockId) => effects.SelectMany(e => e.Do).FirstOrDefault(b => b.BlockId == blockId);

    [EngineFact]
    public void CrystalBeast_IsPlacedInsteadOfGoingToTheGy()
    {
        var card = Monster(32710364, "Crystal Beast Ruby Carbuncle");
        var effects = Translate(32710364, card);
        var place = effects.Single(e => e.Do.Any(b => b.BlockId == PlaceInsteadPassive.BlockId));
        Assert.Empty(place.If);
        Assert.True(Find([place], PlaceInsteadPassive.BlockId)!.Filters.GetValueOrDefault("cards") is null or { ThisCard: true });
        Assert.Equal("If this card is destroyed while in the Monster Zone, you can place it face-up in your Spell & Trap Zone as a Continuous Spell, instead of sending it to the GY.",
            EffectTextGenerator.Describe(place, card));
    }

    [EngineFact]
    public void HeartOfTheBlueEyes_ControlCannotSwitch()
    {
        var rule = Find(Translate(54475145, Monster(54475145, "Heart of the Blue-Eyes")), "passive.self_rule");
        Assert.Equal("control_fixed", rule!.Values["rule"]);
    }

    [EngineFact]
    public void HeraldOfTheArcLight_BanishesMonstersFromTheHandAndMainDeck()
    {
        var card = Monster(79606837, "Herald of the Arc Light", MonsterFrame.Synchro);
        var instead = Find(Translate(79606837, card), "passive.banish_instead");
        Assert.Equal(("monster", "either", "hand_deck"), (instead!.Values["what"], instead.Values["whose"], instead.Values["from"]));
        var project = new CardProject
        {
            Card = new CardData { Id = 100472001, Name = "Arc Herald", Frame = MonsterFrame.Effect, Level = 4 },
            Effects = [new EffectEvent { When = new BlockInstance("when.while_face_up").With("zone", "monster_zone"), Do = [instead] }],
        };
        var lua = ScriptCompiler.Compile(project).Lua;
        Assert.Contains("SetTargetRange(LOCATION_HAND+LOCATION_DECK,LOCATION_HAND+LOCATION_DECK)", lua);
        Assert.DoesNotContain("EFFECT_FLAG_IGNORE_RANGE", lua);
    }

    [EngineFact]
    public void QliphortGenius_IsUnaffectedBySpellsTrapsAndOtherLinkMonsters()
    {
        var immune = Find(Translate(22423493, Monster(22423493, "Qliphort Genius", MonsterFrame.Link)), "passive.immune");
        Assert.Equal("spelltrap_other_link", immune!.Values["whose"]);
    }

    [EngineFact]
    public void ExodiaIncarnate_LosesLp()
    {
        var lose = Find(Translate(83257450, Monster(83257450, "The Unstoppable Exodia Incarnate", MonsterFrame.Fusion)), SetLifeAction.BlockId);
        Assert.Equal(("you", "lose", "1000"), (lose!.Values["who"], lose.Values["how"], lose.Values["amount"]));
    }

    [EngineFact]
    public void Mimighoul_SummonsItselfToTheOpponentsField()
    {
        var card = Monster(81522098, "Mimighoul Dragon");
        card.Abilities = MonsterAbility.Flip;
        var effects = Translate(81522098, card);
        var summon = effects.Single(e => e.Do.Any(b => b.BlockId == "do.special_summon_self"));
        Assert.StartsWith("You can Special Summon this card from your hand to your opponent's field in face-down Defense Position.", EffectTextGenerator.Describe(summon, card));
        // The translated effect plays: it lands face-down on the opponent's field.
        var own = new CardProject { Card = new CardData { Id = 100472002, Name = "Ghoul Copy", Frame = MonsterFrame.Effect, Level = 3, Abilities = MonsterAbility.Flip }, Effects = [summon] };
        new EngineScenario(own).Card(own.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Activate(own.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,0,LOCATION_MZONE,nil,{own.Card.Id}):IsFacedown()").AssertPasses();
    }
}
