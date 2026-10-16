using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official scripts whose ATK/DEF, Level, damage or LP amounts are worked out from the duel, read into the value blocks
/// (passive.stats_value, do.stats_value, do.lp_value), and played in the engine.
/// </summary>
public class ValueTranslationTests
{
    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    private static CardData Monster(long id, int atk = 1000, int def = 1000, int level = 4) =>
        new() { Id = id, Name = "Value " + id, Frame = MonsterFrame.Effect, Level = level, Atk = atk, Def = def, Race = CardRace.Machine, Attribute = CardAttribute.Dark };

    private static CardData SpellCard(long id) => new() { Id = id, Name = "Value " + id, Kind = CardKind.Spell };

    /// <summary>The effect of an official script that uses <paramref name="blockId"/>, translated for <paramref name="card"/>.</summary>
    private static EffectEvent Effect(long official, CardData card, string blockId)
    {
        var result = ScriptTranslator.Translate(Script(official)!, official, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.Do.Any(b => b.BlockId == blockId));
        Assert.True(effect is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var compiled = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect] });
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        Assert.DoesNotContain(ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        return effect;
    }

    private static TranslatedPiece PieceWith(long official, CardData card, string code)
    {
        var result = ScriptTranslator.Translate(Script(official)!, official, card);
        return result.Pieces.First(p => p.Piece.Preview.Contains(code, StringComparison.Ordinal));
    }

    private static CardProject Project(CardData card, EffectEvent effect) => new() { Card = card, Effects = [effect] };

    [EngineFact]
    public void FlareScarab_GainsAtkForEachSpellTrapTheOpponentControls()
    {
        if (Script(89621922) is null) return;
        var card = Monster(100098001, atk: 500);
        var effect = Effect(89621922, card, StatsFromValuePassive.BlockId);
        var b = effect.Do.Single();
        Assert.Equal(("count", "per", "400", "gain", "atk"), (b.Values["value"], b.Values["how"], b.Values["amount"], b.Values["dir"], b.Values["stat"]));
        Assert.True(b.Filters["cards"].ThisCard);
        Assert.Equal(CardOwner.Opponent, b.Filters[ValueSources.CardsKey].Owner);
        Assert.Equal("This card gains 400 ATK for each Spell/Trap your opponent controls.", EffectTextGenerator.Describe(effect, card));
        new EngineScenario(Project(card, effect)).Card(card.Id, 0, "mzone").Card(Spell, 1, "szone", 2).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{card.Id}):GetAttack()==1300").AssertPasses();
    }

    [EngineFact]
    public void GoukiTheGreatOgre_EveryMonsterLosesAtkEqualToItsOwnOriginalDef()
    {
        if (Script(78437364) is null) return;
        var card = Monster(100098002, atk: 2400, def: 1600);
        var effect = Effect(78437364, card, StatsFromValuePassive.BlockId);
        var b = effect.Do.Single();
        Assert.Equal(("its_orig_def", "equal", "lose"), (b.Values["value"], b.Values["how"], b.Values["dir"]));
        Assert.Equal(CardOwner.Either, b.Filters["cards"].Owner);
        Assert.Equal("Face-up monsters on the field lose ATK equal to their original DEF.", EffectTextGenerator.Describe(effect, card));
        new EngineScenario(Project(card, effect)).Card(card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{card.Id}):GetAttack()==800")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{Warrior}):GetAttack()==500").AssertPasses();
    }

    [EngineFact]
    public void FieldEffects_ReadTheMonstersOwnNumbers()
    {
        if (Script(44947065) is null || Script(15335853) is null) return;
        // Burden of the Mighty: "monsters your opponent controls lose 100 ATK for each of their Levels".
        var burden = Effect(44947065, new CardData { Id = 100098003, Name = "Burden", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous }, StatsFromValuePassive.BlockId);
        Assert.Equal(("its_level", "per", "100", "lose"), (burden.Do[0].Values["value"], burden.Do[0].Values["how"], burden.Do[0].Values["amount"], burden.Do[0].Values["dir"]));
        Assert.Equal("Face-up monsters your opponent controls lose 100 ATK for each of their Levels.", EffectTextGenerator.Describe(burden, new CardData { Id = 100098003, Name = "Burden", Kind = CardKind.Spell }));
        // Mecha Phantom Beast Sabre Hawk: "this card's Level is increased by the total Levels of the Tokens you control".
        var hawk = Effect(15335853, Monster(100098004), StatsFromValuePassive.BlockId);
        Assert.Equal(("sum_level", "equal", "gain", "level"), (hawk.Do[0].Values["value"], hawk.Do[0].Values["how"], hawk.Do[0].Values["dir"], hawk.Do[0].Values["stat"]));
        Assert.Equal(31533705, hawk.Do[0].Filters[ValueSources.CardsKey].CardCode);
    }

    [EngineFact]
    public void Bayonater_TheTargetLosesAtkForEachMonsterTheOpponentControls()
    {
        if (Script(47474172) is null) return;
        var card = Monster(100098005);
        var effect = Effect(47474172, card, StatsFromValueAction.BlockId);
        var b = effect.Do.Single();
        Assert.Equal("true", b.Values["target"]);
        Assert.Equal(("count", "per", "1000", "lose", "always"), (b.Values["value"], b.Values["how"], b.Values["amount"], b.Values["dir"], b.Values["duration"]));
        Assert.Equal("Once per turn: You can target 1 face-up monster your opponent controls; it loses 1000 ATK for each monster your opponent controls.", EffectTextGenerator.Describe(effect, card));
        new EngineScenario(Project(card, effect)).Card(card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{Warrior}):GetAttack()==500").AssertPasses();
    }

    [EngineFact]
    public void SelfBoosts_AreLostWhenThisCardIsNegated()
    {
        if (Script(37910722) is null) return;
        // Ascension Sky Dragon: "it gains 800 ATK for each card currently in your hand" (RESET_EVENT+RESETS_STANDARD+RESET_DISABLE).
        var card = Monster(100098006);
        var effect = Effect(37910722, card, StatsFromValueAction.BlockId);
        var b = effect.Do.Single();
        Assert.True(b.Filters["cards"].ThisCard);
        Assert.Equal(("count", "per", "800", "always"), (b.Values["value"], b.Values["how"], b.Values["amount"], b.Values["duration"]));
        Assert.Contains("RESET_EVENT+RESETS_STANDARD+RESET_DISABLE", ScriptCompiler.Compile(Project(card, effect)).Lua);
        // Wild Hope gives itself ATK that outlasts a negation (no RESET_DISABLE): not the same, so it stays script.
        if (Script(51391183) is not null)
            Assert.False(PieceWith(51391183, Monster(100098007), "SetOperation(c51391183.atkop)").Translated);
    }

    [EngineFact]
    public void CemetaryBomb_And_GiftOfTheMysticalElf_WorkOutTheAmountWhenTheyResolve()
    {
        if (Script(51394546) is null || Script(98299011) is null) return;
        var bomb = SpellCard(100098010);
        var burn = Effect(51394546, bomb, LifeFromValueAction.BlockId);
        Assert.Equal(("damage", "opponent", "count", "per", "100"), (burn.Do[0].Values["what"], burn.Do[0].Values["who"], burn.Do[0].Values["value"], burn.Do[0].Values["how"], burn.Do[0].Values["amount"]));
        Assert.Equal("Inflict 100 damage to your opponent for each card in your opponent's GY.", EffectTextGenerator.Describe(burn, bomb));
        new EngineScenario(Project(bomb, burn)).Card(bomb.Id, 0, "hand").Card(Warrior, 1, "grave", 3).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(bomb.Id).ExpectActivations(1).Check("Duel.GetLP(1)==7700").AssertPasses();
        // With nothing in their GY it can't be activated, as the script's "if chk==0" says.
        new EngineScenario(Project(bomb, burn)).Card(bomb.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(bomb.Id).ExpectActivations(0).AssertPasses();

        var elf = SpellCard(100098011);
        var heal = Effect(98299011, elf, LifeFromValueAction.BlockId);
        Assert.Equal("Gain 300 LP for each monster on the field.", EffectTextGenerator.Describe(heal, elf));
        new EngineScenario(Project(elf, heal)).Card(elf.Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone", 2).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(elf.Id).ExpectActivations(1).Check("Duel.GetLP(0)==8900").AssertPasses();
    }

    [EngineFact]
    public void FushiNoTori_GainsLpEqualToTheBattleDamage()
    {
        if (Script(38538445) is null) return;
        var effect = Effect(38538445, Monster(100098012), LifeFromValueAction.BlockId);
        Assert.Equal(("gain", "you", "event", "equal"), (effect.Do[0].Values["what"], effect.Do[0].Values["who"], effect.Do[0].Values["value"], effect.Do[0].Values["how"]));
    }

    [EngineFact]
    public void AmountsThatWouldActDifferently_StayScript()
    {
        if (Script(109401) is null || Script(82377606) is null || Script(14306092) is null) return;
        // Dark Dimension Soldier inflicts its End Phase damage even when there is no Set card: the block can't be activated for 0.
        var soldier = PieceWith(109401, Monster(100098020), "EVENT_PHASE+PHASE_END");
        Assert.False(soldier.Translated);
        Assert.Contains("amount is 0", soldier.Reason);
        // Ally of Justice Unlimiter needs another target than itself, since it Tributes itself.
        Assert.False(PieceWith(82377606, Monster(100098021), "EFFECT_TYPE_IGNITION").Translated);
        // Card.GetCounter(0) counts kinds of counters, not counters: "for each Shark Counter" isn't a value the blocks have.
        Assert.False(PieceWith(14306092, Monster(100098022), "SetCode(EFFECT_UPDATE_ATTACK)").Translated);
    }
}
