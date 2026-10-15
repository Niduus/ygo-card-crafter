using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Unusual mechanics in real duels: Mitsurugi (Tributing, Rituals, "Tribute this card instead"), Dominus (Traps from
/// the hand, "then if"), Elfnote and Infinite Impermanence (zones), Forbidden Crown and Mind Scan.
/// </summary>
public class UniqueEngineTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance F(BlockInstance b, CardFilter f, string key = "cards") { b.Filters[key] = f; return b; }

    private static CardProject Monster(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Test " + id, Level = 4, Atk = 1000, Def = 1000, Race = CardRace.Reptile },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(params BlockInstance[] actions) => new() { When = B("when.ignition"), Do = [.. actions] };

    private static string At(int seq, long id) => $"Duel.GetFieldCard(0,LOCATION_MZONE,{seq})~=nil and Duel.GetFieldCard(0,LOCATION_MZONE,{seq}):IsCode({id})";

    [EngineFact]
    public void OncePerDuel_IsUsedOnlyOnce()
    {
        var effect = Ignition(B("do.draw"));
        effect.Limit = OncePerTurn.Duel;
        var p = Monster(100008000, effect);
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 6).Card(Warrior, 1, "deck", 6)
            .Activate(p.Card.Id, 3).Turns(3).ExpectActivations(1).AssertPasses();
        Assert.Contains("You can only use this effect of \"Test 100008000\" once per Duel.", EffectTextGenerator.Generate(p));
        Assert.Contains("EFFECT_COUNT_CODE_DUEL", ScriptCompiler.Compile(p).Lua);
    }

    [EngineFact]
    public void Tribute_AsAnEffect_AndYourOpponentTributesTheirOwn()
    {
        var p = Monster(100008010, Ignition(
            F(B("do.tribute"), new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true }),
            B("do.opponent_tributes")));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 3)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,1001)")
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,1004)").AssertPasses();
        Assert.Contains("make your opponent Tribute 1 monster they control (their choice)", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void TributeThisCardInstead_SavesAnotherMonster()
    {
        // Mitsurugi no Mikoto, Saji: "If another Reptile monster(s) you control would be destroyed..., you can Tribute this card instead."
        var saji = new CardProject
        {
            Card = new CardData { Id = 100008020, Name = "Saji", Level = 4, Race = CardRace.Reptile },
            Effects = [new EffectEvent
            {
                When = B("when.while_face_up"), Limit = OncePerTurn.Hard,
                Do = [F(B(ReplaceDestroyPassive.BlockId), new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Reptile, FaceUpOnly = true, ExcludeThisCard = true })],
            }],
        };
        var reptile = Monster(100008021);
        var destroyer = Monster(100008022, Ignition(F(B("do.destroy"), new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardCode = 100008021 })));
        new EngineScenario(saji, reptile, destroyer).Card(saji.Card.Id, 0, "mzone").Card(reptile.Card.Id, 0, "mzone").Card(destroyer.Card.Id, 1, "mzone")
            .Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(destroyer.Card.Id).Activate(saji.Card.Id).Turns(2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{reptile.Card.Id})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{saji.Card.Id})").AssertPasses();
        Assert.Equal("If another face-up Reptile monster(s) you control would be destroyed by battle or card effect, you can Tribute this card instead. You can only use this effect of \"Saji\" once per turn.",
            EffectTextGenerator.Generate(saji));
        Assert.Contains("SetCountLimit(1,id)", ScriptCompiler.Compile(saji).Lua);
    }

    [EngineFact]
    public void RitualSummon_TributesMaterialsWhoseLevelsEqualItsLevel()
    {
        var ritual = new CardProject { Card = new CardData { Id = 100008030, Name = "Ritual Reptile", Frame = MonsterFrame.Ritual, Level = 4, Race = CardRace.Reptile, Atk = 2400 } };
        var spell = new CardProject
        {
            Card = new CardData { Id = 100008031, Name = "Ritual Spell", Kind = CardKind.Spell, SpellKind = SpellKind.Ritual },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.ritual_summon")] }],
        };
        new EngineScenario(ritual, spell).Card(spell.Card.Id, 0, "hand").Card(ritual.Card.Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 3)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{ritual.Card.Id})")
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,1001)").AssertPasses();
        Assert.Contains("Ritual Summon 1 Ritual Monster from your hand, by Tributing monsters from your hand or field whose total Levels equal its Level",
            EffectTextGenerator.Generate(spell));
    }

    [EngineFact]
    public void RevealThisCard_IsPaidFromTheHand()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100008040, Name = "Habakiri", Level = 4 },
            Effects = [new EffectEvent { When = B("when.ignition", ("zone", "hand")), Cost = [B("cost.reveal_self")], Do = [B("do.draw")], Limit = OncePerTurn.Duel }],
        };
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 3)
            .Activate(p.Card.Id).ExpectActivations(1).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2").AssertPasses();
        Assert.StartsWith("You can reveal this card in your hand; draw 1 card.", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void OpponentCanDiscard_OrTheirEffectIsNegated()
    {
        // Ame no Murakumo no Mitsurugi: the opponent discards (the harness says yes), so their Spell still resolves.
        var pot = Samples.PotOfGreedClone();
        var murakumo = Monster(100008050, new EffectEvent
        {
            When = B("when.quick", ("timing", "chain_any"), ("zone", "monster_zone")), Limit = OncePerTurn.Hard,
            Do = [B("do.negate_unless")],
        });
        new EngineScenario(pot, murakumo).Card(murakumo.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Card(pot.Card.Id, 1, "hand").Card(Fiend, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Activate(murakumo.Card.Id).Activate(pot.Card.Id).Turns(2).ExpectActivations(2)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,1004)")
            // Drew 1 for turn and 2 from the Spell.
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==3").AssertPasses();
        Assert.Contains("your opponent can discard 1 card, or that effect is negated", EffectTextGenerator.Generate(murakumo));
    }

    [EngineFact]
    public void TrapFromTheHand_WhenYouControlNoCards()
    {
        // Infinite Impermanence: "If you control no cards, you can activate this card from your hand."
        var imperm = new CardProject
        {
            Card = new CardData { Id = 100008060, Name = "Impermanence", Kind = CardKind.Trap },
            Effects =
            [
                new EffectEvent { When = B("when.activate"), Do = [B("do.negate_effects", ("target", "true")), B("do.negate_column")] },
                new EffectEvent
                {
                    When = B("when.while_face_up"),
                    If = [F(B("if.no_cards"), new CardFilter { Owner = CardOwner.You, Zones = Zone.Field, CardType = FilterCardType.Any })],
                    Do = [B(ActivateFromHandPassive.BlockId)],
                },
            ],
        };
        new EngineScenario(imperm).Card(imperm.Card.Id, 0, "hand").Card(EffectFiller, 1, "mzone").Card(Warrior, 0, "deck", 3)
            .Activate(imperm.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{imperm.Card.Id})").AssertPasses();
        var text = EffectTextGenerator.Generate(imperm);
        Assert.Contains("If you control no cards, you can activate this card from your hand.", text);
        Assert.Contains("all other Spell/Trap effects in this column are negated", text);
    }

    [EngineFact]
    public void Dominus_NegatesASummoningEffect_ThenIfATrapIsInYourGY_DestroysIt()
    {
        var summoner = new CardProject
        {
            Card = new CardData { Id = 100008070, Name = "Reviver", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [F(B("do.special_summon"), new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster })] }],
        };
        var dominus = new CardProject
        {
            Card = new CardData { Id = 100008071, Name = "Impulse", Kind = CardKind.Trap },
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.activate", ("timing", "chain_summons")), Limit = OncePerTurn.Hard,
                    Do =
                    [
                        B("do.negate", ("what", "effect")),
                        F(B(OnlyIfAction.BlockId), new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Trap }).Linked(ActionLink.Then),
                        B("do.that_card"),
                        F(B("do.summon_restriction", ("duration", "duel"), ("what", "activate_monster_of"), ("from_hand", "true")),
                            new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Light | CardAttribute.Earth | CardAttribute.Wind }),
                    ],
                },
                new EffectEvent
                {
                    When = B("when.while_face_up"),
                    If = [F(B("if.cards_exist"), new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Field, CardType = FilterCardType.Any })],
                    Do = [B(ActivateFromHandPassive.BlockId)],
                },
            ],
        };
        new EngineScenario(summoner, dominus).Card(dominus.Card.Id, 0, "szone").Card(Trap, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Card(summoner.Card.Id, 1, "hand").Card(Fiend, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Activate(dominus.Card.Id).Activate(summoner.Card.Id).Turns(2).ExpectActivations(2)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,{summoner.Card.Id})").AssertPasses();
        var text = EffectTextGenerator.Generate(dominus);
        Assert.Contains("When a card or effect is activated that includes an effect that Special Summons a monster(s): Negate that effect, then if you have a Trap in your GY, destroy that card.", text);
        Assert.Contains("If you activated this card from your hand, you cannot activate the effects of", text);
        Assert.Contains("for the rest of this Duel.", text);
        Assert.Contains("If your opponent controls a card, you can activate this card from your hand.", text);
    }

    [EngineFact]
    public void Elfnote_SummonsToTheCenterZone_AndSwapsWithIt()
    {
        var lucina = new CardProject
        {
            Card = new CardData { Id = 100008080, Name = "Lucina", Level = 4 },
            Effects = [new EffectEvent { When = B("when.summon_procedure", ("zone", "hand"), ("to", "center")), Limit = OncePerTurn.Hard }],
        };
        new EngineScenario(lucina).Card(lucina.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 3)
            .SpSummon(lucina.Card.Id).Check(At(2, lucina.Card.Id)).AssertPasses();
        Assert.StartsWith("You can Special Summon this card (from your hand) to your center Main Monster Zone.", EffectTextGenerator.Generate(lucina));

        var swapper = Monster(100008081, Ignition(B("do.move_zone", ("what", "swap_center"))));
        // Placed in zones 0, 1 and 2: the swapper moves to the center.
        new EngineScenario(swapper).Card(swapper.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Dragon, 0, "mzone").Card(Warrior, 0, "deck", 3)
            .Activate(swapper.Card.Id).ExpectActivations(1).Check(At(2, swapper.Card.Id)).Check(At(0, Dragon)).AssertPasses();
    }

    [EngineFact]
    public void ForbiddenCrown_AppliesEachTickedEffect()
    {
        var crown = new CardProject
        {
            Card = new CardData { Id = 100008090, Name = "Crown", Kind = CardKind.Spell, SpellKind = SpellKind.QuickPlay },
            Effects = [new EffectEvent
            {
                When = B("when.activate"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.chain_limit")],
                Do = [F(B("do.apply_effects", ("duration", "always"), ("no_tribute", "false")),
                    new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })],
            }],
        };
        string Has(string code) => $"Duel.GetFieldCard(1,LOCATION_MZONE,0):IsHasEffect({code})~=nil";
        new EngineScenario(crown).Card(crown.Card.Id, 0, "hand").Card(EffectFiller, 1, "mzone").Card(Warrior, 0, "deck", 3)
            .Activate(crown.Card.Id).ExpectActivations(1)
            .Check(Has("EFFECT_DISABLE")).Check(Has("EFFECT_CANNOT_ATTACK")).Check(Has("EFFECT_INDESTRUCTABLE_EFFECT")).Check(Has("EFFECT_IMMUNE_EFFECT"))
            .Check(Has("EFFECT_CANNOT_BE_LINK_MATERIAL")).Check($"not ({Has("EFFECT_UNRELEASABLE_SUM")})").AssertPasses();
        var text = EffectTextGenerator.Generate(crown);
        Assert.Contains("● Negate its effects. ● Cannot attack. ● Cannot be destroyed by battle or card effects.", text);
        Assert.DoesNotContain("Cannot be Tributed", text);
        Assert.Contains("Neither player can activate monster effects in response to this card's activation.", text);
        Assert.Contains("Duel.SetChainLimit(", ScriptCompiler.Compile(crown).Lua);
    }

    [EngineFact]
    public void MindScan_RevealsTheHand_AndNegatesTheDeclaredName()
    {
        // In response to the opponent's Spell, it declares that Spell's name: the Spell's effect is negated as it resolves.
        var pot = Samples.PotOfGreedClone();
        var scan = new CardProject
        {
            Card = new CardData { Id = 100008100, Name = "Scan", Level = 4 },
            Effects =
            [
                new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.reveal_hand")] },
                new EffectEvent { When = B("when.quick", ("timing", "chain_any"), ("zone", "monster_zone")), Do = [B("do.declare_negate")], Limit = OncePerTurn.Hard },
            ],
        };
        new EngineScenario(pot, scan).Card(scan.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 3)
            .Card(pot.Card.Id, 1, "hand").Card(Fiend, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Announce(pot.Card.Id).Activate(scan.Card.Id).Activate(pot.Card.Id).Turns(2).ExpectActivations(2)
            .Check("Duel.GetFieldGroup(1,LOCATION_HAND,0):GetFirst():IsPublic()")
            // The Fiend and the card drawn for turn: the Spell drew nothing.
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==2").AssertPasses();
        var text = EffectTextGenerator.Generate(scan);
        Assert.Contains("You can declare 1 card name; negate the activated effects of cards with that original name until the end of this turn.", text);
        Assert.Contains("Your opponent must keep their hand revealed.", text);
    }

    [Fact]
    public void Dodge_AndRitualMaterialsFromTheHand_Wording()
    {
        var mage = new CardProject
        {
            Card = new CardData { Id = 100008110, Name = "Faceless Mage", Level = 4 },
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.self_rule", ("rule", "battle_safe"))] }],
        };
        Assert.Equal("If this card battles a monster, neither can be destroyed by that battle.", EffectTextGenerator.Generate(mage));
        Assert.Contains("EFFECT_INDESTRUCTABLE_BATTLE", ScriptCompiler.Compile(mage).Lua);

        var tribute = F(B("cost.tribute", ("from", "hand_or_field")), new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Reptile });
        var p = Monster(100008111, new EffectEvent { When = B("when.ignition"), Cost = [tribute], Do = [B("do.draw")] });
        Assert.StartsWith("You can Tribute 1 Reptile monster from your hand or field; draw 1 card.", EffectTextGenerator.Generate(p));
        Assert.Contains("Duel.SelectReleaseGroupEx(tp,", ScriptCompiler.Compile(p).Lua);
    }
}

/// <summary>The official cards behind these mechanics read as the new blocks.</summary>
public class UniqueTranslationTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    [EngineTheory]
    [InlineData(18176525L, CardKind.Monster, "passive.replace_destroy")] // Mitsurugi no Mikoto, Saji
    [InlineData(40543231L, CardKind.Monster, "passive.replace_destroy")] // Mitsurugi no Mikoto, Aramasa
    [InlineData(10045474L, CardKind.Trap, ActivateFromHandPassive.BlockId)] // Infinite Impermanence
    [InlineData(66690411L, CardKind.Spell, "passive.reveal_hand")]      // Mind on Air
    public void Mechanics_TranslateToTheNewBlocks(long id, CardKind kind, string block)
    {
        var card = new CardData { Id = id, Name = "Card " + id, Kind = kind, Level = 4, TrapKind = TrapKind.Normal };
        var result = ScriptTranslator.Translate(Script(id), id, card);
        var blocks = result.Pieces.SelectMany(p => p.Effects).SelectMany(e => e.Do).Select(b => b.BlockId).ToList();
        Assert.True(blocks.Contains(block), $"{string.Join(", ", blocks)}\n{string.Join("\n", ScriptTranslator.Untranslated(result))}");
    }

    [EngineFact]
    public void ElfnoteLucina_SummonsItselfToTheCenterZone()
    {
        var card = new CardData { Id = 13597785, Name = "Elfnote Lucina", Level = 4 };
        var result = ScriptTranslator.Translate(Script(13597785), 13597785, card);
        var proc = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.When?.BlockId == SummonProcedureTrigger.BlockId);
        Assert.True(proc is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        Assert.Equal("center", proc!.When!.Values["to"]);
    }
}
