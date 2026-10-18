using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The Elfnote, Power Patron and Crimson Dragon pieces: Summons the opponent can't respond to (Theorealize Past Lull), to a
/// zone a Link Monster points to, "as many as possible" (Crimson Dragon Quetzalcoatl), the monster in the center Main Monster
/// Zone that can't be banished, Tokens with the declared Level that carry a lock (Elfnotes: Quatrain of Succession), a
/// targeted monster moved to another zone (Elfnotes: Aristeia of Trust), the total original ATK of cards, a center monster
/// treated as a Tuner (Junora the Power Patron of Tuning) and an original ATK that becomes a number (Elfnote Seraphim Strelitzia).
/// </summary>
public class ElfnoteTests
{
    private const int Elfnote = 0x1d8;

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static CardPack Pack(params CardProject[] cards)
    {
        var pack = new CardPack { Archetypes = [new Archetype { Code = Elfnote, Name = "Elfnote" }] };
        pack.Cards.AddRange(cards);
        return pack;
    }

    private static string Lua(CardProject p, CardPack? pack = null)
    {
        var r = ScriptCompiler.Compile(p, pack ?? Pack(p));
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    private static string Text(CardProject p, int i = 0, CardPack? pack = null) => EffectTextGenerator.Describe(p.Effects[i], p.Card, pack ?? Pack(p));

    private static CardProject Monster(long id, string name, int atk = 1000, int level = 4, MonsterFrame frame = MonsterFrame.Effect, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = frame, Race = CardRace.Dragon, Attribute = CardAttribute.Fire, Level = level, Atk = atk, Def = 1000 },
        Effects = [.. effects],
    };

    private static CardProject Spell(long id, string name, SpellKind kind, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Kind = CardKind.Spell, SpellKind = kind },
        Effects = [.. effects],
    };

    private static EffectEvent Activate(params BlockInstance[] dos) => new() { When = B("when.activate"), Do = [.. dos] };

    // ---------------------------------------------------------------- Theorealize Past Lull

    /// <summary>"Special Summon 1 Warrior monster from your hand or Deck. Your opponent cannot activate cards or effects when that monster is Special Summoned."</summary>
    private static CardProject PastLull(long id, bool noResponse) => Spell(id, "Lull " + id, SpellKind.Normal, Activate(
        B("do.special_summon").With("no_response", noResponse)
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Warrior })));

    /// <summary>"When your opponent Special Summons a monster(s) (Quick Effect): You can inflict 500 damage to your opponent."</summary>
    private static CardProject Responder(long id) => Monster(id, "Responder", effects: new EffectEvent
    {
        When = B("when.quick").With("timing", "summon_special").With("zone", "monster_zone"),
        Do = [B("do.damage").With("amount", 500)],
    });

    [Fact]
    public void NoResponse_ReadsAndCompilesAsTheorealizePastLull()
    {
        var p = PastLull(100990001, true);
        Assert.Equal("Special Summon 1 Warrior monster from your hand or Deck. Your opponent cannot activate cards or effects when that monster is Special Summoned.", Text(p));
        var lua = Lua(p);
        Assert.Contains("if n>0 and Duel.GetCurrentChain()==1 then", lua);
        Assert.Contains("e1:SetCode(EVENT_CHAIN_END)", lua);
        Assert.Contains("Duel.SetChainLimitTillChainEnd(s.spchainlm", lua);
        // Several monsters: "when those monsters are Special Summoned".
        p.Effects[0].Do[0].Values["count"] = "2";
        Assert.EndsWith("Your opponent cannot activate cards or effects when those monsters are Special Summoned.", Text(p));
    }

    [EngineFact]
    public void NoResponse_TheOpponentCannotRespondToTheSummon()
    {
        const long lull = 100990002, responder = 100990003;
        // Without it, the opponent's Quick Effect inflicts 500 damage when the Warrior is Special Summoned.
        new EngineScenario(PastLull(lull, false), Responder(responder)).Card(lull, 0, "hand").Card(responder, 1, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(lull).Activate(responder).Check("Duel.GetLP(0)==7500").Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").AssertPasses();
        // With it, they cannot.
        new EngineScenario(PastLull(lull, true), Responder(responder)).Card(lull, 0, "hand").Card(responder, 1, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(lull).Activate(responder).Check("Duel.GetLP(0)==8000").Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- to your zone a Link Monster points to

    /// <summary>"You can banish this card from your GY, then target 1 Dragon monster in your GY; Special Summon it to your zone a Link Monster points to."</summary>
    private static CardProject PastLullGy(long id) => Spell(id, "Lull GY " + id, SpellKind.Normal, new EffectEvent
    {
        When = B("when.ignition").With("zone", "graveyard"),
        Cost = [B("cost.this_card").With("how", "banish")],
        Do = [B("do.special_summon").With("target", true).With("zone", "linked")
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, Race = CardRace.Dragon })],
    });

    private static CardProject LinkOne(long id, LinkMarker arrows) => new()
    {
        Card = new CardData { Id = id, Name = "Arrow " + id, Frame = MonsterFrame.Link, Race = CardRace.Cyberse, Attribute = CardAttribute.Light, Atk = 1000, LinkMarkers = arrows },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 1, MaxMaterials = 1 },
    };

    [Fact]
    public void LinkedZone_ReadsAndCompiles()
    {
        var p = PastLullGy(100990010);
        // Targets in the GY read "from your GY" here, as the other targeted Special Summons do.
        Assert.Equal("You can banish this card from your GY, then target 1 Dragon monster from your GY; Special Summon it to your zone a Link Monster points to.", Text(p));
        var lua = Lua(p);
        Assert.Contains("Duel.GetLocationCount(tp,LOCATION_MZONE,tp,LOCATION_REASON_TOFIELD,Duel.GetLinkedZone(tp))>=1", lua);
        Assert.Contains(",POS_FACEUP,Duel.GetLinkedZone(tp))", lua);
    }

    [EngineFact]
    public void LinkedZone_TheMonsterGoesWhereALinkMonsterPoints()
    {
        const long lull = 100990011, link = 100990012;
        // The Link Monster, moved to zone 3, points right to zone 4: the Dragon goes there, not to the free zones 0 to 2.
        new EngineScenario(PastLullGy(lull), LinkOne(link, LinkMarker.Right)).Card(lull, 0, "grave").Card(link, 0, "mzone").Card(Dragon, 0, "grave").Card(Warrior, 0, "deck", 3)
            .Setup($"Duel.MoveSequence(Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{link}),3)")
            .Activate(lull).Check($"Duel.GetFieldCard(0,LOCATION_MZONE,4)~=nil and Duel.GetFieldCard(0,LOCATION_MZONE,4):IsCode({Dragon})").AssertPasses();
        // No Link Monster: it can't be activated.
        new EngineScenario(PastLullGy(lull)).Card(lull, 0, "grave").Card(Dragon, 0, "grave").Card(Warrior, 0, "deck", 3)
            .Activate(lull).ExpectActivations(0).AssertPasses();
    }

    // ---------------------------------------------------------------- Elfnotes: Quatrain of Succession

    private static CardFilter Center() => new() { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, CenterZone = true };

    /// <summary>"The monster in your center Main Monster Zone cannot be banished by your opponent's card effects."</summary>
    private static CardProject Quatrain(long id) => Spell(id, "Quatrain " + id, SpellKind.Continuous, new EffectEvent
    {
        When = B("when.while_face_up").With("zone", "spell_trap_zone"),
        Do = [B("passive.cards_rule").With("rule", "cannot_banish_opp").With("cards", Center())],
    });

    /// <summary>"You can banish 1 monster your opponent controls."</summary>
    private static CardProject Banisher(long id) => Monster(id, "Banisher", effects: new EffectEvent
    {
        When = B("when.ignition").With("zone", "monster_zone"),
        Do = [B("do.banish").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
    });

    [Fact]
    public void CannotBeBanished_ReadsAndCompilesAsQuatrainOfSuccession()
    {
        var p = Quatrain(100990020);
        Assert.Equal("The monster in your center Main Monster Zone cannot be banished by your opponent's card effects.", Text(p));
        var lua = Lua(p);
        Assert.Contains("e1:SetCode(EFFECT_CANNOT_REMOVE)", lua);
        Assert.Contains("e1:SetTargetRange(1,1)", lua);
        Assert.Contains("r&REASON_EFFECT~=0 and r&REASON_REDIRECT==0 and rp==1-tp", lua);
        // Welcome Home's protection reads the same way.
        var home = Spell(100990021, "Welcome Home", SpellKind.Continuous, new EffectEvent
        {
            When = B("when.while_face_up").With("zone", "spell_trap_zone"),
            Do = [B("passive.protect_cards").With("by", "effect").With("cards", Center())],
        });
        Assert.Equal("The monster in your center Main Monster Zone cannot be destroyed by card effects.", Text(home));
    }

    [EngineFact]
    public void CannotBeBanished_TheCenterMonsterStays()
    {
        const long quatrain = 100990022, banisher = 100990023;
        // Your Warriors in zones 0, 1 and 2; on your opponent's turn their monster banishes 1: never the one in the center.
        new EngineScenario(Quatrain(quatrain), Banisher(banisher)).Card(quatrain, 0, "hand").Card(Warrior, 0, "mzone", 3).Card(banisher, 1, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(quatrain).Activate(banisher).Turns(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_REMOVED,0)==1").Check("Duel.GetFieldCard(0,LOCATION_MZONE,2)~=nil").AssertPasses();
        // Only the center monster: there is nothing it can banish.
        new EngineScenario(Quatrain(quatrain), Banisher(banisher)).Card(quatrain, 0, "hand").Card(Warrior, 0, "mzone").Card(banisher, 1, "mzone")
            .Setup("Duel.MoveSequence(Duel.GetFieldCard(0,LOCATION_MZONE,0),2)")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(quatrain).Activate(banisher).Turns(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_REMOVED,0)==0").Check("Duel.GetFieldCard(0,LOCATION_MZONE,2)~=nil").AssertPasses();
    }

    // ---------------------------------------------------------------- Tokens with the declared Level, and their lock

    private const long SeraphimToken = 100990031;

    private static CardProject Token() => new()
    {
        Card = new CardData { Id = SeraphimToken, Name = "Elfnote Seraphim Token", Frame = MonsterFrame.Token, Race = CardRace.Plant, Attribute = CardAttribute.Fire, Level = 1, Atk = 0, Def = 0, Abilities = MonsterAbility.Tuner, Archetypes = [Elfnote] },
    };

    /// <summary>
    /// "You can send 1 other Spell/Trap from your hand or field to the GY, then declare a Level from 1 to 4; Special Summon 1
    /// "Elfnote Seraphim Token" (Plant/Tuner/FIRE/ATK 0/DEF 0) with the declared Level. While that Token is in the Monster
    /// Zone, the player who Summoned it cannot Special Summon from the Extra Deck, except "Elfnote" monsters."
    /// </summary>
    private static CardProject Succession(long id) => Spell(id, "Succession " + id, SpellKind.Continuous, new EffectEvent
    {
        When = B("when.ignition").With("zone", "spell_trap_zone"),
        Cost = [B("cost.move_cards").With("how", "grave").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Field, CardType = FilterCardType.SpellTrap, ExcludeThisCard = true })],
        Do =
        [
            B(DeclareAction.BlockId).With("what", "level").With("min", 1).With("max", 4),
            B("do.tokens").With("token", SeraphimToken).With("level", "declared").With("lock", true)
                .With("except", new CardFilter { CardType = FilterCardType.Monster, Archetype = Elfnote }),
        ],
    });

    [Fact]
    public void DeclaredLevelToken_ReadsAndCompilesAsQuatrainOfSuccession()
    {
        var p = Succession(100990030);
        var pack = Pack(p, Token());
        Assert.Equal("You can send 1 other Spell/Trap from your hand or field to the GY, then declare a Level from 1 to 4; Special Summon 1 \"Elfnote Seraphim Token\" " +
                     "(Plant/Tuner/FIRE/ATK 0/DEF 0) with the declared Level. While that Token is in the Monster Zone, the player who Summoned it cannot Special Summon " +
                     "from the Extra Deck, except \"Elfnote\" monsters.", Text(p, pack: pack));
        var lua = Lua(p, pack);
        Assert.Contains("e1:SetCode(EFFECT_CHANGE_LEVEL)", lua);
        Assert.Contains("le:SetAbsoluteRange(tp,1,0)", lua);
        Assert.Contains("le:SetCode(EFFECT_CANNOT_SPECIAL_SUMMON)", lua);
        Assert.Contains("token:RegisterEffect(le,true)", lua);
        // Without a declaration it can't take the declared Level.
        p.Effects[0].Do.RemoveAt(0);
        Assert.Contains(ScriptCompiler.Compile(p, pack).Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("Declare a Level"));
    }

    private static CardProject AnyLink(long id, bool elfnote)
    {
        var link = LinkOne(id, LinkMarker.Bottom);
        if (elfnote) link.Card.Archetypes = [Elfnote];
        return link;
    }

    [EngineFact]
    public void DeclaredLevelToken_HasTheLevelAndLocksTheExtraDeck()
    {
        const long spell = 100990032, plain = 100990033, elf = 100990034;
        // Level 3 is declared; then a plain Link-1 can't be Link Summoned while the Token is there.
        new EngineScenario(Succession(spell), Token(), AnyLink(plain, false)).Card(spell, 0, "hand").Card(EngineScenario.Spell, 0, "hand").Card(plain, 0, "extra").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Activate(spell, 2).Declare(3).SpSummon(plain)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{SeraphimToken}):GetLevel()==3")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_EXTRA,0,1,nil,{plain})").AssertPasses();
        // An "Elfnote" Link-1 can.
        new EngineScenario(Succession(spell), Token(), AnyLink(elf, true)).Card(spell, 0, "hand").Card(EngineScenario.Spell, 0, "hand").Card(elf, 0, "extra").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Activate(spell, 2).Declare(3).SpSummon(elf)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{elf})").AssertPasses();
    }

    // ---------------------------------------------------------------- Elfnotes: Aristeia of Trust

    private static CardProject Aristeia(long id) => Monster(id, "Aristeia " + id, effects: new EffectEvent
    {
        When = B("when.ignition").With("zone", "monster_zone"),
        Do = [B("do.move_zone").With("what", "target")],
        Limit = OncePerTurn.Soft,
    });

    [Fact]
    public void TargetAndMove_ReadsAndCompilesAsAristeiaOfTrust()
    {
        var p = Aristeia(100990040);
        Assert.Equal("Once per turn: You can target 1 face-up monster you control; move that monster you control to another of your Main Monster Zones.", Text(p));
        var lua = Lua(p);
        Assert.Contains("e1:SetProperty(EFFECT_FLAG_CARD_TARGET)", lua);
        Assert.Contains("Duel.MoveSequence(mc,math.log(seq,2))", lua);
    }

    [EngineFact]
    public void TargetAndMove_TheTargetMovesToAFreeZone()
    {
        const long id = 100990041;
        new EngineScenario(Aristeia(id)).Card(Warrior, 0, "mzone").Card(id, 0, "mzone").Card(Warrior, 0, "deck", 3).Activate(id)
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,2)~=nil").Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==2").ExpectActivations(1).AssertPasses();
    }

    // ---------------------------------------------------------------- Crimson Dragon Quetzalcoatl

    private static CardFilter DragonSynchros(Zone zones) => new() { Owner = CardOwner.You, Zones = zones, CardType = FilterCardType.Monster, Race = CardRace.Dragon, MonsterKind = MonsterKind.Synchro };

    private static CardProject Quetzalcoatl(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Quetzalcoatl " + id, Frame = MonsterFrame.Synchro, Race = CardRace.Dragon, Attribute = CardAttribute.Fire, Level = 8, Atk = 1000, Def = 1000 },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.while_face_up").With("zone", "monster_zone"),
                Do = [B(StatsFromValuePassive.BlockId).With("value", "sum_orig_atk")
                    .With(ValueSources.CardsKey, new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro, FaceUpOnly = true, ExcludeThisCard = true })],
            },
            new EffectEvent
            {
                When = B("when.ignition").With("zone", "monster_zone"),
                Do =
                [
                    B("do.special_summon").With("as_many", true).With("cards", DragonSynchros(Zone.Graveyard)),
                    B("do.summon_restriction").With("what", "special").With("cards", new CardFilter { CardType = FilterCardType.Monster }).Linked(ActionLink.AlsoAfterThat),
                ],
            },
        ],
    };

    private static CardProject Synchro(long id, int atk) => Monster(id, "Synchro " + id, atk, 6, MonsterFrame.Synchro);

    [Fact]
    public void TotalOriginalAtk_AndAsManyAsPossible_ReadAsQuetzalcoatl()
    {
        var p = Quetzalcoatl(100990050);
        Assert.Equal("This card gains ATK equal to the total original ATK of all other face-up Synchro Monsters you control.", Text(p));
        Assert.Equal("You can Special Summon as many Dragon Synchro Monsters from your GY as possible, also you cannot Special Summon monsters for the rest of this turn.", Text(p, 1));
        var lua = Lua(p);
        Assert.Contains(":GetSum(Card.GetBaseAttack)", lua);
        Assert.Matches(@"if Duel\.IsPlayerAffectedByEffect\(tp,59822133\) then (ft\d*)=1 end", lua);
        Assert.Matches(@"(g\d*)=\1:Select\(tp,(ft\d*),\2,nil\)", lua);
    }

    [EngineFact]
    public void TotalOriginalAtk_AndAsManyAsPossible_PlayInTheEngine()
    {
        const long q = 100990051, s1 = 100990052, s2 = 100990053;
        // 1000 + the other Synchros' 1200 and 1800 (not the Warrior's).
        new EngineScenario(Quetzalcoatl(q), Synchro(s1, 1200), Synchro(s2, 1800)).Card(q, 0, "mzone").Card(s1, 0, "mzone").Card(s2, 0, "mzone").Card(Warrior, 0, "mzone")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{q}):GetAttack()==4000").AssertPasses();
        // 3 Dragon Synchros in the GY and 2 free zones: 2 are Special Summoned.
        new EngineScenario(Quetzalcoatl(q), Synchro(s1, 1200)).Card(q, 0, "mzone").Card(Warrior, 0, "mzone", 2).Card(s1, 0, "grave", 3).Card(Warrior, 0, "deck", 3)
            // As if they had been Synchro Summoned before: they can be Special Summoned from the GY.
            .Setup("for tc in aux.Next(Duel.GetFieldGroup(0,LOCATION_GRAVE,0)) do tc:CompleteProcedure() end")
            .Activate(q).Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==5").Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_GRAVE,0,nil,{s1})==1").AssertPasses();
    }

    // ---------------------------------------------------------------- Junora the Power Patron of Tuning

    private static CardProject Junora(long id)
    {
        var p = Monster(id, "Junora " + id, 2500, 6, MonsterFrame.Synchro);
        p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 2, MaxMaterials = 2, SynchroAsTuner = true };
        return p;
    }

    [Fact]
    public void CenterAsTuner_ReadsAndCompilesAsJunora()
    {
        var p = Junora(100990060);
        Assert.StartsWith("1 Tuner + 2 non-Tuner monsters\nFor this card's Synchro Summon, you can treat 1 monster in your center Main Monster Zone as a Tuner.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("aux.AddSynchroMixProcedure(c,s.synmatfilter,nil,nil,aux.NonTuner(nil),2,2)", lua);
        Assert.Contains("return c:IsTuner(syncard) or c:IsLocation(LOCATION_MZONE) and c:GetSequence()==2", lua);
    }

    [EngineFact]
    public void CenterAsTuner_ReadsTheOfficialScript()
    {
        var script = File.ReadAllText(Path.Combine(EngineScripts!, "c5914858.lua"));
        var card = new CardData { Id = 5914858, Name = "Junora the Power Patron of Tuning", Frame = MonsterFrame.Synchro, Level = 10 };
        var proc = ScriptTranslator.Translate(script, card.Id, card).Pieces.Select(p => p.Procedure).Single(p => p is not null)!;
        Assert.True(proc is { SynchroAsTuner: true, MinMaterials: 2, MaxMaterials: 2 });
        Assert.True(proc.SynchroAsTunerFilter.CenterZone);
    }

    /// <summary>The pieces of an official script that translate, by their titles, with the card text they then read as.</summary>
    private static Dictionary<string, string> Official(long id, CardData card)
    {
        var script = File.ReadAllText(Path.Combine(EngineScripts!, $"c{id}.lua"));
        var pieces = ScriptTranslator.Translate(script, id, card).Pieces.Where(p => p.Translated && p.Effects.Count() == 1).ToList();
        return pieces.ToDictionary(p => p.Piece.Title, p => EffectTextGenerator.Describe(p.Effects.First(), card, Pack()));
    }

    [EngineFact]
    public void OfficialScripts_ReadBackAsTheNewBlocks()
    {
        // Elfnotes: Quatrain of Succession's protection.
        var quatrain = Official(58809685, new CardData { Id = 58809685, Name = "Elfnotes: Quatrain of Succession", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous });
        Assert.Equal("The monster in your center Main Monster Zone cannot be banished by your opponent's card effects.", quatrain["Cannot Banish"]);
        // Elfnote Seraphim Strelitzia's original ATK, and Crimson Dragon Quetzalcoatl's total original ATK.
        var strelitzia = Official(42302563, new CardData { Id = 42302563, Name = "Elfnote Seraphim Strelitzia", Frame = MonsterFrame.Synchro, Level = 8, Atk = 2500 });
        Assert.Equal("While this card is in the center Main Monster Zone, this card's original ATK becomes 3000.", strelitzia["Atkup"]);
        var quetzalcoatl = Official(29053656, new CardData { Id = 29053656, Name = "Crimson Dragon Quetzalcoatl", Frame = MonsterFrame.Synchro, Level = 10, Atk = 3000 });
        Assert.Equal("This card gains ATK equal to the total original ATK of all other face-up Synchro Monsters you control.", quetzalcoatl["Atk"]);
    }

    [EngineFact]
    public void CenterAsTuner_SynchroSummonsWithTheCenterMonster()
    {
        const long id = 100990061, two = 100990062;
        var lv2 = Monster(two, "Level Two", level: 2);
        // Three Level 2 non-Tuners in zones 0 to 2: the center one is the Tuner.
        new EngineScenario(Junora(id), lv2).Card(id, 0, "extra").Card(two, 0, "mzone", 3).SpSummon(id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
        // None of them in the center: no Tuner.
        new EngineScenario(Junora(id), lv2).Card(id, 0, "extra").Card(two, 0, "mzone", 3)
            .Setup($"Duel.MoveSequence(Duel.GetFieldCard(0,LOCATION_MZONE,2),4)").SpSummon(id)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
    }

    // ---------------------------------------------------------------- Elfnote Seraphim Strelitzia

    private static CardProject Strelitzia(long id) => Monster(id, "Strelitzia " + id, effects: new EffectEvent
    {
        When = B("when.while_face_up").With("zone", "monster_zone"),
        If = [B("if.zone")],
        Do = [B(StatsFromValuePassive.BlockId).With("dir", "becomes").With("stat", "orig_atk").With("value", ValueSources.NumberSource).With("number", 3000)],
    });

    [Fact]
    public void OriginalAtkBecomesANumber_ReadsAndCompilesAsStrelitzia()
    {
        var p = Strelitzia(100990070);
        // Card text also says it last ("..., while in the center Main Monster Zone"), as Strelitzia does.
        Assert.Equal("While this card is in the center Main Monster Zone, this card's original ATK becomes 3000.", Text(p));
        var lua = Lua(p);
        Assert.Contains("SetCode(EFFECT_SET_BASE_ATTACK)", lua);
        Assert.Contains("return (3000)", lua);
    }

    [EngineFact]
    public void OriginalAtkBecomesANumber_OnlyInTheCenter()
    {
        const long id = 100990071;
        new EngineScenario(Strelitzia(id)).Card(Warrior, 0, "mzone", 2).Card(id, 0, "mzone")
            .Check($"Duel.GetFieldCard(0,LOCATION_MZONE,2):GetAttack()==3000").AssertPasses();
        new EngineScenario(Strelitzia(id)).Card(id, 0, "mzone")
            .Check($"Duel.GetFieldCard(0,LOCATION_MZONE,0):GetAttack()==1000").AssertPasses();
    }

    // ---------------------------------------------------------------- a cost card or a target paired with the step that uses it

    private static CardRequirement Differs(CardRef that, string property = "ref_orig_attribute") =>
        new() { Property = property, Op = RequirementOp.IsNot, Values = [(long)that] };

    /// <summary>"You can send 1 monster from your hand to the GY; Special Summon 1 "Elfnote" monster with a different original Attribute from your Deck."</summary>
    private static CardProject WelcomeHome(long id) => Monster(id, "Welcome " + id, effects: new EffectEvent
    {
        When = B("when.ignition").With("zone", "monster_zone"),
        Cost = [B("cost.move_cards").With("how", "grave").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster })],
        Do = [B("do.special_summon").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Archetype = Elfnote, Requirements = [Differs(CardRef.Cost)] })],
    });

    private static CardProject Elf(long id, CardAttribute attribute)
    {
        var p = Monster(id, "Elf " + id);
        p.Card.Attribute = attribute;
        p.Card.Archetypes = [Elfnote];
        return p;
    }

    [Fact]
    public void PairedCost_OnlyACardTheSummonCanDifferFrom()
    {
        var p = WelcomeHome(100990080);
        var lua = Lua(p);
        Assert.Contains("s.ccpaircost=Group.FromCards(c)", lua);
        Assert.Contains("and s.paircost(c,e,tp)", lua);
        Assert.Contains("if s.ccpaircost then return s.ccpaircost end", lua);
    }

    [EngineFact]
    public void PairedCost_PicksACostCardWithAFollowUp()
    {
        const long home = 100990081, fire = 100990082, water = 100990083;
        // The FIRE monster in the hand comes first, but only the WATER one leaves a FIRE "Elfnote" monster to Summon.
        new EngineScenario(WelcomeHome(home), Elf(fire, CardAttribute.Fire), Elf(water, CardAttribute.Water)).Card(home, 0, "mzone").Card(fire, 0, "hand").Card(water, 0, "hand")
            .Card(fire, 0, "deck").Activate(home)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{fire})").Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{water})").AssertPasses();
        // Only a FIRE monster to send: it can't be activated.
        new EngineScenario(WelcomeHome(home), Elf(fire, CardAttribute.Fire)).Card(home, 0, "mzone").Card(fire, 0, "hand").Card(fire, 0, "deck").Activate(home)
            .ExpectActivations(0).AssertPasses();
    }

    /// <summary>
    /// "You can target 1 Level 7 or higher Synchro Monster on the field; Special Summon 1 Dragon Synchro Monster from your Extra
    /// Deck with the same Level as the targeted monster. (This is treated as a Synchro Summon.)" (Crimson Dragon).
    /// </summary>
    private static CardProject CrimsonDragon(long id) => Monster(id, "Crimson " + id, effects: new EffectEvent
    {
        When = B("when.ignition").With("zone", "monster_zone"),
        Do =
        [
            B(TargetCardsAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, MonsterKind = MonsterKind.Synchro, LevelMin = 7 }),
            B("do.special_summon").With("as", "synchro").With("cards", new CardFilter
            {
                Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, Race = CardRace.Dragon, MonsterKind = MonsterKind.Synchro,
                Requirements = [new CardRequirement { Property = "ref_level", Op = RequirementOp.Is, Values = [(long)CardRef.Target] }],
            }),
        ],
    });

    [EngineFact]
    public void PairedTarget_PicksATargetWithAMatchingExtraDeckMonster()
    {
        const long crimson = 100990090, seven = 100990091, eight = 100990092, dragon8 = 100990093;
        var lv7 = Monster(seven, "Seven", level: 7, frame: MonsterFrame.Synchro);
        var lv8 = Monster(eight, "Eight", level: 8, frame: MonsterFrame.Synchro);
        var d8 = Monster(dragon8, "Dragon Eight", level: 8, frame: MonsterFrame.Synchro);
        lv7.Card.Race = lv8.Card.Race = CardRace.Warrior;
        Assert.Contains("s.ccpairtarget=Group.FromCards(c)", Lua(CrimsonDragon(crimson)));
        // The Level 7 Synchro comes first, but only the Level 8 one has a Dragon Synchro of its Level in the Extra Deck.
        new EngineScenario(CrimsonDragon(crimson), lv7, lv8, d8).Card(crimson, 0, "mzone").Card(seven, 0, "mzone").Card(eight, 0, "mzone").Card(dragon8, 0, "extra")
            .Activate(crimson).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{dragon8})").AssertPasses();
        // Only the Level 7 one: it can't be activated.
        new EngineScenario(CrimsonDragon(crimson), lv7, d8).Card(crimson, 0, "mzone").Card(seven, 0, "mzone").Card(dragon8, 0, "extra")
            .Activate(crimson).ExpectActivations(0).AssertPasses();
    }

    // ---------------------------------------------------------------- Ars Magna "Citrinitas": effects activated as Quick Effects

    private const int PowerPatron = 0x1cd;

    /// <summary>"You can activate the effects of "Elfnote" monsters you control as Quick Effects while you control a "Power Patron" monster."</summary>
    private static CardProject Citrinitas(long id, Kind kind = Kind.Spell)
    {
        var effect = new EffectEvent
        {
            When = B("when.while_face_up").With("zone", kind == Kind.Spell ? "spell_trap_zone" : "monster_zone"),
            If = [B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = PowerPatron, FaceUpOnly = true })],
            Do = [B(QuickEffectsPassive.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = Elfnote })],
        };
        return kind == Kind.Spell ? Spell(id, "Citrinitas " + id, SpellKind.Continuous, effect) : Monster(id, "Citrinitas " + id, effects: effect);
    }

    private enum Kind { Spell, Monster }

    /// <summary>An "Elfnote" monster: "You can inflict 500 damage to your opponent", a Quick Effect while <paramref name="granter"/> lets its player.</summary>
    private static CardProject Receiver(long id, long granter)
    {
        var p = Monster(id, "Receiver " + id, effects: new EffectEvent
        {
            When = B("when.ignition").With("zone", "monster_zone").With("quick_by", granter),
            Do = [B("do.damage").With("amount", 500)],
            Limit = OncePerTurn.Soft,
        });
        p.Card.Archetypes = [Elfnote];
        return p;
    }

    private static CardProject Patron(long id)
    {
        var p = Monster(id, "Patron " + id);
        p.Card.Archetypes = [PowerPatron];
        return p;
    }

    [Fact]
    public void QuickEffects_ReadAndCompileAsCitrinitas()
    {
        var granter = Citrinitas(100990100);
        var pack = Pack(granter);
        pack.Archetypes.Add(new Archetype { Code = PowerPatron, Name = "Power Patron" });
        Assert.Equal("While you control a face-up \"Power Patron\" monster, you can activate the effects of \"Elfnote\" monsters you control as Quick Effects.", Text(granter, pack: pack));
        var lua = Lua(granter, pack);
        Assert.Contains("aux.quick_effect_filter[id]=s.quickeffects", lua);
        Assert.Contains("SetCode(id)", lua);
        var receiver = Lua(Receiver(100990101, granter.Card.Id));
        Assert.Contains("return not aux.IsCanBeQuickEffect(e:GetHandler(),tp,100990100)", receiver);
        Assert.Contains("e1b:SetType(EFFECT_TYPE_QUICK_O)", receiver);
        Assert.Contains("e1b:SetCode(EVENT_FREE_CHAIN)", receiver);
    }

    [EngineFact]
    public void QuickEffects_TheReceiverActsOnTheOpponentsTurn()
    {
        const long granter = 100990102, receiver = 100990103, patron = 100990104;
        // Your opponent's "Elfnote" monster uses its effect during your turn, while they control the granter and a "Power Patron" monster.
        new EngineScenario(Citrinitas(granter, Kind.Monster), Receiver(receiver, granter), Patron(patron)).Card(granter, 1, "mzone").Card(receiver, 1, "mzone").Card(patron, 1, "mzone")
            .Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3).Activate(receiver).Check("Duel.GetLP(0)==7500").AssertPasses();
        // Without a "Power Patron" monster it is an Ignition effect again: not on your turn.
        new EngineScenario(Citrinitas(granter, Kind.Monster), Receiver(receiver, granter)).Card(granter, 1, "mzone").Card(receiver, 1, "mzone")
            .Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3).Activate(receiver).Check("Duel.GetLP(0)==8000").AssertPasses();
    }
}
