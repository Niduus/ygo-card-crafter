using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The details of the activation, Summon, attack and battle WHENs: each option fires in a duel when it should and stays
/// off when it shouldn't.
/// </summary>
public class TriggerDetailTests
{
    private const long Id = 100111001, OppCard = 100111002, OppMonster = 100111003, Attacker = 100111004, Strong = 100111005;

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    /// <summary>A monster (or a Trap, for "activate this card") that gains 1000 LP when its WHEN fires.</summary>
    private static CardProject Watcher(BlockInstance when, CardKind kind = CardKind.Monster) => new()
    {
        Card = kind == CardKind.Monster
            ? new CardData { Id = Id, Name = "Watcher", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000, Race = CardRace.Fiend }
            : new CardData { Id = Id, Name = "Watcher", Kind = kind },
        Effects = [new EffectEvent { When = when, Do = [new BlockInstance("do.gain_lp").With("amount", 1000)] }],
    };

    private static CardProject OppSpell(params BlockInstance[] does) => new()
    {
        Card = new CardData { Id = OppCard, Name = "Opp Spell", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = does.Length == 0 ? [new BlockInstance("do.draw")] : [.. does] }],
    };

    /// <summary>The opponent's monster with an ignition effect used from <paramref name="zone"/>.</summary>
    private static CardProject OppIgnition(string zone = "auto") => new()
    {
        Card = new CardData { Id = OppMonster, Name = "Opp Monster", Frame = MonsterFrame.Effect, Level = 4, Atk = 500, Def = 500 },
        Effects = [new EffectEvent { When = B("when.ignition").With("zone", zone), Do = [new BlockInstance("do.draw")] }],
    };

    private static EngineScenario Duel(params CardProject[] cards) => new EngineScenario(cards).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4);

    // ---------------------------------------------------------------- activations

    [Fact]
    public void Activations_ReadLikeCards()
    {
        var trap = B(ActivationTrigger.BlockId).With("mode", "activate").With("who", "either").With("what", "card_or_monster");
        Assert.StartsWith("When a Spell/Trap Card, or monster effect, is activated: Gain 1000 LP.", EffectTextGenerator.Generate(Watcher(trap, CardKind.Trap)));
        var quick = B(ActivationTrigger.BlockId).With("what", "spell_card").With("targets", "this_card");
        Assert.StartsWith("When your opponent activates a Spell Card that targets this card (Quick Effect): You can gain 1000 LP.", EffectTextGenerator.Generate(Watcher(quick)));
        var damage = B(ActivationTrigger.BlockId).With("who", "either").With("includes", "damage_you");
        Assert.StartsWith("When a card or effect is activated that would inflict damage to you (Quick Effect): ", EffectTextGenerator.Generate(Watcher(damage)));
        var link = B(ActivationTrigger.BlockId).With("who", "either").With("what", "monster").With("targets", "this_card");
        link.Filters["card"] = new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Link, Zones = Zone.None };
        Assert.StartsWith("When a Link Monster's effect is activated that targets this card (Quick Effect): ", EffectTextGenerator.Generate(Watcher(link)));
        var gem = B(ActivationTrigger.BlockId).With("targets", "cards");
        gem.Filters["target_cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Dragon };
        Assert.StartsWith("When your opponent activates a card or effect that targets a Dragon monster(s) you control (Quick Effect): ", EffectTextGenerator.Generate(Watcher(gem)));
        // "If this card is in your GY": where the card is, when nothing else says it.
        var gy = B(ActivationTrigger.BlockId).With("zone", "graveyard");
        Assert.StartsWith("When your opponent activates a card or effect while this card is in your GY (Quick Effect): ", EffectTextGenerator.Generate(Watcher(gy)));
    }

    [EngineTheory]
    [InlineData("spell_card", true)]
    [InlineData("trap_card", false)]
    [InlineData("card_or_monster", true)]
    public void ATrapActivatedInResponse(string what, bool responds)
    {
        var trap = Watcher(B(ActivationTrigger.BlockId).With("mode", "activate").With("what", what), CardKind.Trap);
        Assert.Contains("EFFECT_TYPE_ACTIVATE", ScriptCompiler.Compile(trap).Lua);
        Duel(trap, OppSpell()).Card(Id, 0, "szone").Card(OppCard, 1, "hand")
            .Activate(OppCard).Activate(Id).Turns(2)
            .Check(responds ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("card_or_monster", true)]
    [InlineData("monster", true)]
    [InlineData("spelltrap_card", false)]
    public void AMonsterEffectOrASpellTrapCard(string what, bool responds)
    {
        Duel(Watcher(B(ActivationTrigger.BlockId).With("what", what)), OppIgnition()).Card(Id, 0, "mzone").Card(OppMonster, 1, "mzone")
            .Activate(OppMonster).Activate(Id).Turns(2)
            .Check(responds ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("gy", true)]
    [InlineData("field_gy", true)]
    [InlineData("hand", false)]
    public void WhereItIsActivated(string from, bool responds)
    {
        Duel(Watcher(B(ActivationTrigger.BlockId).With("what", "monster").With("from", from)), OppIgnition("graveyard")).Card(Id, 0, "mzone").Card(OppMonster, 1, "grave")
            .Activate(OppMonster).Activate(Id).Turns(2)
            .Check(responds ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    /// <summary>The opponent's Spell destroys 1 monster you control (targeted).</summary>
    private static CardProject TargetingSpell()
    {
        var destroy = B("do.destroy").With("target", true).With("count", 1);
        destroy.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        return OppSpell(destroy);
    }

    [EngineTheory]
    [InlineData("your_monster", Warrior, true)]
    [InlineData("on_field", Warrior, true)]
    [InlineData("this_card", Warrior, false)]
    [InlineData("dragons", Warrior, false)]
    [InlineData("dragons", Dragon, true)]
    public void WhatItTargets(string targets, long onField, bool responds)
    {
        // The watcher waits in the hand; the opponent's Spell targets the only monster on the field.
        var when = B(ActivationTrigger.BlockId).With("zone", "hand").With("targets", targets == "dragons" ? "cards" : targets);
        if (targets == "dragons") when.Filters["target_cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Dragon };
        Duel(Watcher(when), TargetingSpell()).Card(Id, 0, "hand").Card(onField, 0, "mzone").Card(OppCard, 1, "hand")
            .Activate(OppCard).Activate(Id).Turns(2)
            .Check(responds ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineFact]
    public void ThatTargetsThisCard()
    {
        Duel(Watcher(B(ActivationTrigger.BlockId).With("targets", "this_card")), TargetingSpell()).Card(Id, 0, "mzone").Card(OppCard, 1, "hand")
            .Activate(OppCard).Activate(Id).Turns(2)
            .Check("Duel.GetLP(0)==9000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("damage_you", true, true)]
    [InlineData("damage_you", false, false)]
    [InlineData("damage_opponent", true, false)]
    public void ThatWouldInflictDamage(string includes, bool burns, bool responds)
    {
        var spell = burns ? OppSpell(new BlockInstance("do.damage").With("amount", 500)) : OppSpell();
        Duel(Watcher(B(ActivationTrigger.BlockId).With("includes", includes)), spell).Card(Id, 0, "mzone").Card(OppCard, 1, "hand")
            .Activate(OppCard).Activate(Id).Turns(2)
            .Check($"Duel.GetLP(0)=={8000 - (burns ? 500 : 0) + (responds ? 1000 : 0)}").AssertPasses();
    }

    [EngineTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AnotherCardsEffect_LeavesThisCardOut(bool other, bool responds)
    {
        // "If you activate another card's effect" next to this card's own ignition effect.
        var when = B(ActivationTrigger.BlockId).With("mode", "trigger").With("who", "you");
        when.Filters["card"] = new CardFilter { CardType = FilterCardType.Any, Zones = Zone.None, ExcludeThisCard = other };
        var p = Watcher(when);
        p.Effects.Add(new EffectEvent { When = B("when.ignition"), Do = [new BlockInstance("do.draw")] });
        Duel(p).Card(Id, 0, "mzone").Activate(Id, 2)
            .Check(responds ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [Theory]
    [InlineData("yes", "EFFECT_FLAG_DAMAGE_STEP+EFFECT_FLAG_DAMAGE_CAL")]
    [InlineData("step", "EFFECT_FLAG_DAMAGE_STEP)")]
    public void TheDamageStepChoice_SetsItsFlags(string choice, string flags)
    {
        var lua = ScriptCompiler.Compile(Watcher(B(ActivationTrigger.BlockId).With("damage_step", choice))).Lua;
        Assert.Contains(flags, lua);
    }

    // ---------------------------------------------------------------- Summons about to happen

    private static CardProject SummonNegater(BlockInstance when) => new()
    {
        Card = new CardData { Id = Id, Name = "Horn", Kind = CardKind.Trap },
        Effects = [new EffectEvent { When = when, Do = [B("do.negate_summon").With("and", "destroy")] }],
    };

    [Fact]
    public void SummonAttempts_ReadLikeCards()
    {
        Assert.StartsWith("When your opponent would Special Summon a monster(s): Negate the Summon",
            EffectTextGenerator.Generate(SummonNegater(B(SummonAttemptTrigger.BlockId).With("mode", "activate"))));
        Assert.StartsWith("When a monster(s) would be Summoned: Negate the Summon",
            EffectTextGenerator.Generate(SummonNegater(B(SummonAttemptTrigger.BlockId).With("mode", "activate").With("who", "either").With("how", "any"))));
        Assert.StartsWith("When your opponent would Special Summon exactly 1 monster: ",
            EffectTextGenerator.Generate(SummonNegater(B(SummonAttemptTrigger.BlockId).With("mode", "activate").With("count", "one"))));
    }

    [EngineTheory]
    [InlineData("normal", "any", true)]
    [InlineData("any", "any", true)]
    [InlineData("special", "any", false)]
    [InlineData("normal", "dragon", false)]
    public void NegatesTheNormalSummon(string how, string which, bool negates)
    {
        var when = B(SummonAttemptTrigger.BlockId).With("mode", "activate").With("how", how).With("check", how == "any" ? "ready" : "current");
        if (which == "dragon") when.Filters["cards"] = new CardFilter { Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster, Race = CardRace.Dragon };
        Duel(SummonNegater(when)).Card(Id, 0, "szone").Card(Fiend, 1, "hand")
            .Summon(Fiend).Activate(Id).Turns(2)
            .Check(negates ? "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0 and Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==1" : "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1")
            .AssertPasses();
    }

    [EngineTheory]
    [InlineData("opponent", true)]
    [InlineData("you", false)]
    public void OnlyTheRightPlayersSummon(string who, bool negates)
    {
        Duel(SummonNegater(B(SummonAttemptTrigger.BlockId).With("mode", "activate").With("how", "normal").With("who", who))).Card(Id, 0, "szone").Card(Fiend, 1, "hand")
            .Summon(Fiend).Activate(Id).Turns(2)
            .Check(negates ? "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0" : "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- attacks

    private static CardProject OppAttacker(int atk = 2000) => new()
    {
        Card = new CardData { Id = Attacker, Name = "Attacker", Frame = MonsterFrame.Normal, Level = 4, Atk = atk, Def = 1000, Race = CardRace.Beast },
    };

    [Fact]
    public void Attacks_ReadLikeCards()
    {
        var involving = B(AttackDeclaredTrigger.BlockId).With("mode", "trigger").With("how", "involving");
        involving.Filters["mine"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior };
        Assert.StartsWith("When an attack is declared involving your Warrior monster: You can gain 1000 LP.", EffectTextGenerator.Generate(Watcher(involving)));
        var targeted = B(AttackDeclaredTrigger.BlockId).With("how", "targeted");
        Assert.StartsWith("When a monster you control is targeted for an attack (Quick Effect): ", EffectTextGenerator.Generate(Watcher(targeted)));
        var trap = B(AttackDeclaredTrigger.BlockId).With("mode", "activate").With("how", "targeting");
        Assert.StartsWith("When an opponent's monster declares an attack targeting a monster you control: Gain 1000 LP.", EffectTextGenerator.Generate(Watcher(trap, CardKind.Trap)));
    }

    [EngineTheory]
    [InlineData(CardRace.Warrior, true)]
    [InlineData(CardRace.Dragon, false)]
    public void AnAttackInvolvingYourMonster(CardRace race, bool fires)
    {
        var when = B(AttackDeclaredTrigger.BlockId).With("mode", "trigger").With("how", "involving").With("zone", "hand");
        when.Filters["mine"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = race };
        Duel(Watcher(when), OppAttacker()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            // The 2000-ATK attacker destroys the 1500-ATK Warrior for 500 damage.
            .Check(fires ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==7500").AssertPasses();
    }

    [EngineTheory]
    [InlineData("targeted", true)]
    [InlineData("direct", false)]
    public void IsTargetedForAnAttack(string how, bool fires)
    {
        var when = B(AttackDeclaredTrigger.BlockId).With("how", how).With("zone", "hand");
        Assert.Contains(how == "targeted" ? "EVENT_BE_BATTLE_TARGET" : "EVENT_ATTACK_ANNOUNCE", ScriptCompiler.Compile(Watcher(when)).Lua);
        Duel(Watcher(when), OppAttacker()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check(fires ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==7500").AssertPasses();
    }

    [EngineFact]
    public void ATrapActivatedWhenAnAttackIsDeclared()
    {
        var trap = Watcher(B(AttackDeclaredTrigger.BlockId).With("mode", "activate"), CardKind.Trap);
        Duel(trap, OppAttacker()).Card(Id, 0, "szone").Card(Warrior, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check("Duel.GetLP(0)==8500").AssertPasses();
    }

    // ---------------------------------------------------------------- battle windows

    [Fact]
    public void BattleWindows_ReadLikeCards()
    {
        var calc = B(BattleWindowTrigger.BlockId).With("who", "yours");
        calc.Filters["mine"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior };
        Assert.StartsWith("If your Warrior monster battles, during damage calculation (Quick Effect): ", EffectTextGenerator.Generate(Watcher(calc)));
        var end = B(BattleWindowTrigger.BlockId).With("mode", "trigger").With("window", "end").With("versus", "opponent");
        Assert.StartsWith("At the end of the Damage Step, if this card battled an opponent's monster: You can ", EffectTextGenerator.Generate(Watcher(end)));
        var before = B(BattleWindowTrigger.BlockId).With("mode", "trigger").With("window", "before").With("who", "attacks").With("versus", "cards");
        before.Filters["vs"] = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Position = FilterPosition.Defense };
        Assert.StartsWith("If this card attacks a Defense Position monster, before damage calculation: ", EffectTextGenerator.Generate(Watcher(before)));
        var damage = B(BattleWindowTrigger.BlockId).With("who", "damage_you");
        Assert.StartsWith("During damage calculation, if you would take battle damage (Quick Effect): ", EffectTextGenerator.Generate(Watcher(damage)));
    }

    [EngineTheory]
    [InlineData(CardRace.Warrior, true)]
    [InlineData(CardRace.Dragon, false)]
    public void DuringDamageCalculation_IfYourMonsterBattles(CardRace race, bool fires)
    {
        var when = B(BattleWindowTrigger.BlockId).With("who", "yours").With("zone", "hand");
        when.Filters["mine"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = race };
        Duel(Watcher(when), OppAttacker()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check(fires ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==7500").AssertPasses();
    }

    [EngineTheory]
    [InlineData(2000, true)]
    [InlineData(1000, false)]
    public void DuringDamageCalculation_IfYouWouldTakeBattleDamage(int atk, bool fires)
    {
        // A 1000-ATK attacker loses to the 1500-ATK Warrior: no damage to you.
        Duel(Watcher(B(BattleWindowTrigger.BlockId).With("who", "damage_you").With("zone", "hand")), OppAttacker(atk)).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check(atk == 2000 ? (fires ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==7500") : (fires ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000")).AssertPasses();
    }

    [EngineTheory]
    [InlineData("attacked", "start", true)]
    [InlineData("attacks", "start", false)]
    [InlineData("this_card", "after", true)]
    public void ThisCardsOwnBattle(string who, string window, bool fires)
    {
        var p = Watcher(B(BattleWindowTrigger.BlockId).With("mode", "trigger").With("who", who).With("window", window));
        p.Card.Def = 3000;
        p.Card.Atk = 0;
        Assert.Contains("EFFECT_TYPE_SINGLE", ScriptCompiler.Compile(p).Lua);
        // The watcher in Attack Position with 0 ATK: the 2000-ATK attacker destroys it for 2000 damage.
        Duel(p, OppAttacker()).Card(Id, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check(fires ? "Duel.GetLP(0)==7000" : "Duel.GetLP(0)==6000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("opponent", true)]
    [InlineData("monster", true)]
    [InlineData("any", true)]
    public void AtTheEndOfTheDamageStep_IfThisCardBattledAnOpponentsMonster(string versus, bool fires)
    {
        // A 3000-ATK watcher destroys the attacker and stays: "if this card battled an opponent's monster".
        var p = Watcher(B(BattleWindowTrigger.BlockId).With("mode", "trigger").With("window", "end").With("versus", versus));
        p.Card.Atk = 3000;
        Duel(p, OppAttacker()).Card(Id, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check(fires ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000")
            .Check($"Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- this card, in detail

    [Fact]
    public void ThisCardDetails_ReadLikeCards()
    {
        var gy = B(ThisCardDetailTrigger.BlockId).With("event", "destroyed").With("from", "mzone").With("now", "gy");
        Assert.StartsWith("If this card in the Monster Zone is destroyed and sent to the GY: You can gain 1000 LP.", EffectTextGenerator.Generate(Watcher(gy)));
        var leaves = B(ThisCardDetailTrigger.BlockId).With("event", "leaves").With("face", "up").With("by", "battle_or_opponent_effect");
        Assert.StartsWith("If this face-up card leaves the field because of battle or an opponent's card effect: ", EffectTextGenerator.Generate(Watcher(leaves)));
        var set = B(ThisCardDetailTrigger.BlockId).With("face", "down").With("yours", true).With("by", "opponent_destroy_effect").With("wording", "when").With("optional", false);
        Assert.StartsWith("When this Set card in your possession is destroyed by an opponent's card effect and sent to the GY: Gain 1000 LP.", EffectTextGenerator.Generate(Watcher(set)));
        var material = B(ThisCardDetailTrigger.BlockId).With("event", "material").With("material", "link").With("now", "gy");
        Assert.StartsWith("If this card is sent to the GY as material for a Link Summon: ", EffectTextGenerator.Generate(Watcher(material)));
    }

    [EngineTheory]
    [InlineData("by", "opponent_effect", true)]
    [InlineData("by", "effect", true)]
    [InlineData("by", "battle_or_opponent_effect", true)]
    [InlineData("by", "battle", false)]
    [InlineData("by", "cost", false)]
    [InlineData("from", "mzone", true)]
    [InlineData("from", "szone", false)]
    [InlineData("face", "up", true)]
    [InlineData("face", "down", false)]
    [InlineData("now", "gy", true)]
    [InlineData("now", "banished", false)]
    [InlineData("yours", "true", true)]
    public void ThisCard_DestroyedByTheOpponentsSpell(string detail, string value, bool fires)
    {
        var when = B(ThisCardDetailTrigger.BlockId).With("event", "destroyed");
        when.Values[detail] = value;
        Duel(Watcher(when), TargetingSpell()).Card(Id, 0, "mzone").Card(OppCard, 1, "hand")
            .Activate(OppCard).Activate(Id).Turns(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==1")
            .Check(fires ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("battle", true)]
    [InlineData("battle_or_opponent_effect", true)]
    [InlineData("effect", false)]
    // Battle with the opponent's monster: destroyed "by an opponent's card".
    [InlineData("opponent", true)]
    [InlineData("opponent_effect", false)]
    public void ThisCard_DestroyedByBattle(string by, bool fires)
    {
        // The 2000-ATK attacker destroys the 1000-ATK watcher for 1000 damage.
        Duel(Watcher(B(ThisCardDetailTrigger.BlockId).With("event", "destroyed").With("by", by)), OppAttacker()).Card(Id, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check(fires ? "Duel.GetLP(0)==8000" : "Duel.GetLP(0)==7000").AssertPasses();
    }

    // ---------------------------------------------------------------- cards, in detail

    [Fact]
    public void CardsDetails_ReadLikeCards()
    {
        var both = B(CardsDetailTrigger.BlockId).With("event", "normal").With("whose", "summoner");
        var p = Watcher(both);
        p.Effects[0].AlsoWhen.Add(B(CardsDetailTrigger.BlockId).With("event", "special").With("whose", "summoner"));
        Assert.StartsWith("If you Normal or Special Summon a monster(s): You can gain 1000 LP.", EffectTextGenerator.Generate(p));
        var battle = B(CardsDetailTrigger.BlockId).With("event", "battle_destroyed").With("other", "other");
        Assert.StartsWith("If another monster(s) you control is destroyed by battle: ", EffectTextGenerator.Generate(Watcher(battle)));
        var deck = B(CardsDetailTrigger.BlockId).With("event", "sent_gy").With("whose", "before").With("from", "deck").With("wording", "when");
        deck.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.None, CardType = FilterCardType.Any };
        Assert.StartsWith("When a card(s) is sent to the GY from your opponent's Deck: ", EffectTextGenerator.Generate(Watcher(deck)));
        var extra = B(CardsDetailTrigger.BlockId).With("event", "special").With("whose", "summoner").With("summon_from", "extra");
        extra.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.None, CardType = FilterCardType.Monster };
        Assert.StartsWith("If your opponent Special Summons a monster(s) from the Extra Deck: ", EffectTextGenerator.Generate(Watcher(extra)));
    }

    [Theory]
    [InlineData("step", "EFFECT_FLAG_DAMAGE_STEP")]
    [InlineData("no", "EFFECT_FLAG_DELAY)")]
    public void CardsDetails_DamageStepChoice_SetsItsFlags(string choice, string flags)
    {
        var lua = ScriptCompiler.Compile(Watcher(B(CardsDetailTrigger.BlockId).With("event", "destroyed").With("damage_step", choice))).Lua;
        Assert.Contains(flags, lua);
    }

    [EngineTheory]
    [InlineData(CardRace.Warrior, false, true)]
    [InlineData(CardRace.Dragon, false, false)]
    [InlineData(CardRace.Warrior, true, true)]
    [InlineData(CardRace.Dragon, true, false)]
    public void YourMonsterIsDestroyedByBattle(CardRace race, bool previous, bool fires)
    {
        var when = B(CardsDetailTrigger.BlockId).With("event", "battle_destroyed").With("zone", "hand").With("previous", previous);
        when.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = race };
        Assert.Contains(previous ? "GetPreviousRaceOnField" : "IsRace", ScriptCompiler.Compile(Watcher(when)).Lua);
        // The 2000-ATK attacker destroys the 1500-ATK Warrior for 500 damage.
        Duel(Watcher(when), OppAttacker()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check(fires ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==7500").AssertPasses();
    }

    [EngineTheory]
    [InlineData("control", CardOwner.You, "step", true)]
    [InlineData("control", CardOwner.Opponent, "step", false)]
    [InlineData("before", CardOwner.You, "step", true)]
    [InlineData("before", CardOwner.Opponent, "step", false)]
    // A monster destroyed by battle is destroyed in the Damage Step.
    [InlineData("control", CardOwner.You, "no", false)]
    public void WhoseMonsterWasDestroyed(string whose, CardOwner owner, string damageStep, bool fires)
    {
        var when = B(CardsDetailTrigger.BlockId).With("event", "destroyed").With("whose", whose).With("zone", "hand").With("damage_step", damageStep);
        when.Filters["cards"] = new CardFilter { Owner = owner, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        Duel(Watcher(when), OppAttacker()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check(fires ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==7500").AssertPasses();
    }

    [EngineTheory]
    [InlineData(CardOwner.Opponent, "normal", true)]
    [InlineData(CardOwner.You, "normal", false)]
    [InlineData(CardOwner.Either, "normal", true)]
    [InlineData(CardOwner.Opponent, "special", false)]
    public void WhoSummonedIt(CardOwner summoner, string ev, bool fires)
    {
        var when = B(CardsDetailTrigger.BlockId).With("event", ev).With("whose", "summoner");
        when.Filters["cards"] = new CardFilter { Owner = summoner, Zones = Zone.None, CardType = FilterCardType.Monster };
        Duel(Watcher(when)).Card(Id, 0, "mzone").Card(Fiend, 1, "hand")
            .Summon(Fiend).Activate(Id).Turns(2)
            .Check(fires ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("any", true)]
    [InlineData("other", false)]
    [InlineData("not_this", false)]
    public void ThisCardsOwnSummon_CountsUnlessAnotherIsAsked(string other, bool fires)
    {
        var when = B(CardsDetailTrigger.BlockId).With("event", "normal").With("whose", "summoner").With("other", other);
        Duel(Watcher(when)).Card(Id, 0, "hand")
            .Summon(Id).Activate(Id)
            .Check(fires ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    // ---------------------------------------------------------------- this card destroys a monster by battle

    [Theory]
    [InlineData("opponent", "If this card destroys an opponent's monster by battle: ", "aux.bdocon")]
    [InlineData("any", "If this card destroys a monster by battle: ", "aux.bdcon")]
    [InlineData("monster", "If this card destroys a monster by battle: ", "GetBattleTarget():IsType(TYPE_MONSTER)")]
    public void DestroysByBattle_Versus(string versus, string text, string lua)
    {
        var p = Watcher(B("when.destroys_by_battle").With("versus", versus));
        Assert.StartsWith(text, EffectTextGenerator.Generate(p));
        Assert.Contains(lua, ScriptCompiler.Compile(p).Lua);
    }

    [EngineTheory]
    [InlineData("opponent")]
    [InlineData("any")]
    [InlineData("monster")]
    public void DestroysByBattle_TheAttacker(string versus)
    {
        var p = Watcher(B("when.destroys_by_battle").With("versus", versus));
        p.Card.Atk = 3000;
        Duel(p, OppAttacker()).Card(Id, 0, "mzone").Card(Attacker, 1, "mzone")
            .Attack(Attacker).Activate(Id).Turns(2)
            .Check("Duel.GetLP(0)==9000").Check("Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==1").AssertPasses();
    }
}
