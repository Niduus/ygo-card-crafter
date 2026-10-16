using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Gemini and Union monsters, Trap Monsters, cards placed on top of or under the Deck, banishing face-down and battle
/// positions: built from blocks and read from official scripts, then played in the engine.
/// </summary>
public class MechanicsTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance With(BlockInstance b, string key, CardFilter f) { b.Filters[key] = f; return b; }

    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    /// <summary>
    /// The whole card, as the editor would apply it: every part translated, and the blocks compile cleanly. Older scripts
    /// write their own passcode as a number, so the card keeps the official one.
    /// </summary>
    private static CardProject Applied(long official, CardData card)
    {
        var project = new CardProject { Card = card, CustomLua = Script(official)!, UseCustomLua = true };
        var result = ScriptTranslator.ApplyTo(project, null, out var applied);
        Assert.True(applied, string.Join("\n", ScriptTranslator.Untranslated(result))
            + string.Join("\n", ScriptCompiler.Compile(new CardProject { Card = card, Effects = result.Pieces.SelectMany(p => p.Effects).ToList() }).Diagnostics));
        var compiled = ScriptCompiler.Compile(project);
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics));
        Assert.DoesNotContain(ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        return project;
    }

    private static string Find(long code, int player, string loc) => $"Duel.GetFirstMatchingCard(Card.IsCode,{player},{loc},0,nil,{code})";

    private static CardData Monster(long id, CardRace race = CardRace.Warrior, int atk = 1200, MonsterAbility abilities = MonsterAbility.None) =>
        new() { Id = id, Name = "Mechanics " + id, Frame = MonsterFrame.Effect, Level = 4, Atk = atk, Def = 1000, Race = race, Attribute = CardAttribute.Light, Abilities = abilities };

    // ---------------------------------------------------------------- Gemini

    private static CardProject GeminiDrawer(long id) => new()
    {
        Card = Monster(id, abilities: MonsterAbility.Gemini),
        Effects = [new EffectEvent { When = B("when.ignition"), If = [B(GeminiCondition.BlockId)], Do = [B("do.draw")] }],
    };

    [EngineFact]
    public void Gemini_TheEffectWorksOnceTheMonsterIsSummonedAgain()
    {
        var gemini = GeminiDrawer(100171001);
        Assert.Contains("aux.EnableDualAttribute(c)", ScriptCompiler.Compile(gemini).Lua);
        Assert.Contains("aux.IsDualState(e)", ScriptCompiler.Compile(gemini).Lua);
        new EngineScenario(gemini).Card(gemini.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Summon(gemini.Card.Id).Activate(gemini.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .Check($"{Find(gemini.Card.Id, 0, "LOCATION_MZONE")}:IsDualState()").AssertPasses();
    }

    [EngineFact]
    public void Gemini_WhileItIsANormalMonster_TheEffectCannotBeUsed()
    {
        var gemini = GeminiDrawer(100171002);
        new EngineScenario(gemini).Card(gemini.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(gemini.Card.Id).ExpectActivations(0)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    [Fact]
    public void Gemini_TextListsTheEffectsUnderTheGeminiRule()
    {
        var gemini = GeminiDrawer(100171003);
        Assert.Equal("This card is treated as a Normal Monster while face-up on the field or in the GY. While this card is a Normal Monster on the field, " +
                     "you can Normal Summon it to have it become an Effect Monster with this effect." + EffectTextGenerator.OptionLine + "You can draw 1 card.",
            EffectTextGenerator.Generate(gemini));
        gemini.Effects.Add(new EffectEvent { When = B("when.ignition"), If = [B(GeminiCondition.BlockId)], Do = [B("do.damage", ("amount", "500"))] });
        Assert.StartsWith(GeminiCondition.Rule(2), EffectTextGenerator.Generate(gemini));
        Assert.EndsWith(EffectTextGenerator.OptionLine + "You can inflict 500 damage to your opponent.", EffectTextGenerator.Generate(gemini));
    }

    [Fact]
    public void Gemini_TheBlockNeedsAGeminiMonster()
    {
        var notGemini = GeminiDrawer(100171004);
        notGemini.Card.Abilities = MonsterAbility.None;
        Assert.True(ScriptCompiler.Compile(notGemini).HasErrors);
    }

    [EngineFact]
    public void Gemini_ChemicritterHydronHawk_Translated()
    {
        if (Script(55100740) is null) return;
        // "● You can discard 1 card, then target 1 Gemini monster in your GY; Special Summon it in Defense Position."
        var card = Monster(55100740, abilities: MonsterAbility.Gemini);
        var p = Applied(55100740, card);
        Assert.Contains(p.Effects, e => GeminiCondition.Has(e));
        Assert.StartsWith(GeminiCondition.Rule(1), EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(card.Id, 0, "mzone").Card(card.Id, 0, "grave").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 4)
            .Summon(card.Id).Activate(card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==2")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    [EngineFact]
    public void Gemini_AncientGearKnight_Translated()
    {
        if (Script(39303359) is null) return;
        // "return aux.IsDualState(e) and Duel.GetAttacker()==e:GetHandler()": the Gemini block and the other part's own block.
        var p = Applied(39303359, Monster(39303359, CardRace.Machine, 1800, MonsterAbility.Gemini));
        var effect = p.Effects.Single(e => GeminiCondition.Has(e));
        Assert.Equal(2, effect.If.Count);
        Assert.Contains("aux.IsDualState(e)", ScriptCompiler.Compile(p).Lua);
    }

    [EngineFact]
    public void RedEyesBlackFlareDragon_ItsOwnOriginalATK_IsNotADestroyedMonstersATK()
    {
        if (Script(30079770) is null) return;
        // "inflict damage equal to this card's original ATK" (local atk=c:GetBaseAttack()) once read as "equal to that
        // monster's original ATK", the ATK of cards a step before affected: here there is none. It stays script.
        var result = ScriptTranslator.Translate(Script(30079770)!, 30079770, Monster(30079770, CardRace.Dragon, 2400, MonsterAbility.Gemini));
        Assert.False(result.Complete);
        Assert.DoesNotContain(result.Pieces.SelectMany(p => p.Effects).SelectMany(e => e.Do), b => b.Values.GetValueOrDefault("equal") == "orig_atk");
    }

    // ---------------------------------------------------------------- Spirit

    [Fact]
    public void Spirit_TheTextSaysItReturnsToTheHand()
    {
        var spirit = new CardProject { Card = Monster(100171010, abilities: MonsterAbility.Spirit) };
        spirit.Card.CannotBeSpecialSummoned = true;
        Assert.Equal("Cannot be Special Summoned. During the End Phase of the turn this card is Normal Summoned or flipped face-up: Return it to the hand.",
            EffectTextGenerator.Generate(spirit));
    }

    // ---------------------------------------------------------------- Union

    private static CardProject UnionCard(long id) => new()
    {
        Card = Monster(id, CardRace.Machine, 500, MonsterAbility.Union),
        Effects =
        [
            new EffectEvent { When = B("when.while_face_up"), Do = [With(B(UnionPassive.BlockId), "cards", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior })] },
            new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.equipped", ("rule", "gain_atk"), ("amount", "500"))] },
        ],
    };

    [EngineFact]
    public void Union_EquipsItselfToAMonsterYouControl_WhichGainsATK()
    {
        var union = UnionCard(100171020);
        Assert.Contains("aux.EnableUnionAttribute(c,s.unionfilter)", ScriptCompiler.Compile(union).Lua);
        new EngineScenario(union).Card(union.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(union.Card.Id).ExpectActivations(1)
            .Check($"{Find(union.Card.Id, 0, "LOCATION_SZONE")}:GetEquipTarget():IsCode({Warrior})")
            .Check($"{Find(Warrior, 0, "LOCATION_MZONE")}:GetAttack()==2000").AssertPasses();
    }

    [EngineFact]
    public void Union_CannotEquipToAMonsterItsFilterRefuses()
    {
        var union = UnionCard(100171021);
        new EngineScenario(union).Card(union.Card.Id, 0, "mzone").Card(Dragon, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(union.Card.Id).ExpectActivations(0)
            .Check($"{Find(union.Card.Id, 0, "LOCATION_MZONE")}~=nil").AssertPasses();
    }

    [Fact]
    public void Union_Text()
    {
        Assert.Equal("Once per turn, you can either: Target 1 Warrior monster you control; equip this card to that target, OR: Unequip this card and Special Summon it. " +
                     "If the equipped monster would be destroyed by battle or card effect, destroy this card instead. The equipped monster gains 500 ATK.",
            EffectTextGenerator.Generate(UnionCard(100171022)));
    }

    [EngineFact]
    public void Union_HeavyMechSupportPlatform_Translated()
    {
        if (Script(23265594) is null) return;
        // Two copies: one equips itself to the other Machine monster, which gains 500 ATK and DEF.
        var card = Monster(23265594, CardRace.Machine, 500, MonsterAbility.Union);
        var p = Applied(23265594, card);
        Assert.Equal(CardRace.Machine, p.Effects.SelectMany(e => e.Do).Single(b => b.BlockId == UnionPassive.BlockId).Filters["cards"].Race);
        new EngineScenario(p).Card(card.Id, 0, "mzone", 2).Card(Warrior, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"{Find(card.Id, 0, "LOCATION_MZONE")}:GetAttack()==1000")
            .Check($"{Find(card.Id, 0, "LOCATION_MZONE")}:GetDefense()==1500").AssertPasses();
    }

    // ---------------------------------------------------------------- Trap Monsters

    private static CardProject TrapMonster(long id, string position = "faceup") => new()
    {
        Card = new CardData { Id = id, Name = "Mechanics Trap " + id, Kind = CardKind.Trap, TrapKind = TrapKind.Continuous },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B(TrapMonsterAction.BlockId, ("race", "Rock"), ("attribute", "Dark"), ("level", "4"), ("atk", "1800"), ("def", "1000"), ("position", position))],
            },
        ],
    };

    [EngineFact]
    public void TrapMonster_BecomesAMonsterWithItsStats()
    {
        var trap = TrapMonster(100171030);
        var c = Find(trap.Card.Id, 0, "LOCATION_MZONE");
        new EngineScenario(trap).Card(trap.Card.Id, 0, "szone").Card(Warrior, 0, "deck", 4)
            .Activate(trap.Card.Id).ExpectActivations(1)
            .Check($"{c}:IsType(TYPE_TRAP) and {c}:IsType(TYPE_MONSTER) and {c}:IsType(TYPE_EFFECT)")
            .Check($"{c}:GetLevel()==4 and {c}:GetAttack()==1800 and {c}:GetDefense()==1000 and {c}:IsRace(RACE_ROCK) and {c}:IsAttribute(ATTRIBUTE_DARK)")
            // The database holds the stats, as official Trap Monsters' does: its original Level and ATK are the monster's.
            .Check($"{c}:GetOriginalLevel()==4 and {c}:GetTextAttack()==1800")
            .Check($"{c}:IsSummonType(SUMMON_TYPE_SPECIAL+SUMMON_VALUE_SELF)")
            // A Trap Monster takes no Spell & Trap Zone.
            .Check("Duel.GetLocationCount(0,LOCATION_SZONE)==5").AssertPasses();
        Assert.Equal("Special Summon this card as an Effect Monster (Rock/DARK/Level 4/ATK 1800/DEF 1000). (This card is also still a Trap.)", EffectTextGenerator.Generate(trap));
    }

    [EngineFact]
    public void TrapMonster_InDefensePosition_WithNoFreeZone_CannotBeActivated()
    {
        var trap = TrapMonster(100171031, "defense");
        new EngineScenario(trap).Card(trap.Card.Id, 0, "szone").Card(Warrior, 0, "deck", 4)
            .Activate(trap.Card.Id).ExpectActivations(1)
            .Check($"{Find(trap.Card.Id, 0, "LOCATION_MZONE")}:IsPosition(POS_FACEUP_DEFENSE)").AssertPasses();
        new EngineScenario(trap).Card(trap.Card.Id, 0, "szone").Card(Warrior, 0, "mzone", 5).Card(Warrior, 0, "deck", 4)
            .Activate(trap.Card.Id).ExpectActivations(0).AssertPasses();
    }

    [Fact]
    public void TrapMonster_WritesItsStatsIntoTheDatabase_AndOnlyWorksOnTraps()
    {
        var trap = TrapMonster(100171032);
        var pack = new CardPack { Name = "Trap Monsters" };
        pack.Cards.Add(trap);
        using var dir = new TempDir();
        var export = PackExporter.Export(pack, dir.Path);
        var row = Database.CdbFile.Read(export.DatabasePath).Single(r => r.Id == trap.Card.Id);
        Assert.Equal((4L, 1800L, 1000L, (long)CardRace.Rock, (long)CardAttribute.Dark), (row.Level, row.Atk, row.Def, row.Race, row.Attribute));
        var spell = TrapMonster(100171033);
        spell.Card.Kind = CardKind.Spell;
        Assert.True(ScriptCompiler.Compile(spell).HasErrors);
    }

    [EngineFact]
    public void TrapMonster_MetalReflectSlime_Translated()
    {
        if (Script(26905245) is null) return;
        var card = new CardData { Id = 26905245, Name = "Slime", Kind = CardKind.Trap, TrapKind = TrapKind.Continuous };
        var p = Applied(26905245, card);
        var block = p.Effects.SelectMany(e => e.Do).Single(b => b.BlockId == TrapMonsterAction.BlockId);
        Assert.Equal(("Aqua", "Water", "10", "0", "3000", "defense"),
            (block.Values["race"], block.Values["attribute"], block.Values["level"], block.Values["atk"], block.Values["def"], block.Values["position"]));
        var c = Find(card.Id, 0, "LOCATION_MZONE");
        new EngineScenario(p).Card(card.Id, 0, "szone").Card(Warrior, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"{c}:IsPosition(POS_FACEUP_DEFENSE) and {c}:GetDefense()==3000 and {c}:GetLevel()==10")
            // "If Summoned this way, this card cannot attack."
            .Check($"{c}:IsHasEffect(EFFECT_CANNOT_ATTACK)~=nil").AssertPasses();
    }

    // ---------------------------------------------------------------- the Deck

    private static CardProject DeckSpell(long id, CardFilter cards, string where = "top", bool target = false) => new()
    {
        Card = new CardData { Id = id, Name = "Mechanics Spell " + id, Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [With(B(PlaceOnDeckAction.BlockId, ("where", where), ("target", target ? "true" : "false")), "cards", cards)] }],
    };

    [EngineFact]
    public void PlaceOnDeck_ATargetInTheGY_GoesOnTop()
    {
        var spell = DeckSpell(100171040, new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }, target: true);
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "grave").Card(Dragon, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check($"Duel.GetDecktopGroup(0,1):GetFirst():IsCode({Warrior})").AssertPasses();
        Assert.Equal("Target 1 monster from your GY; place it on top of the Deck.", EffectTextGenerator.Generate(spell));
    }

    [EngineFact]
    public void PlaceOnDeck_ACardFromTheHand_GoesToTheBottom()
    {
        var spell = DeckSpell(100171041, new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Any }, "bottom");
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "hand").Card(Dragon, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_DECK,0,nil,{Warrior}):GetSequence()==0")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
        Assert.Equal("Place 1 card from your hand on the bottom of the Deck.", EffectTextGenerator.Generate(spell));
    }

    [EngineFact]
    public void PlaceOnDeck_TakeAMonsterFromYourDeck_AndPlaceItOnTop()
    {
        var spell = DeckSpell(100171042, new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Warrior });
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck").Card(Dragon, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check($"Duel.GetDecktopGroup(0,1):GetFirst():IsCode({Warrior})").AssertPasses();
        Assert.Equal("Take 1 Warrior monster from your Deck and place it on top of your Deck.", EffectTextGenerator.Generate(spell));
        var bottom = DeckSpell(100171043, new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster }, "bottom");
        Assert.True(ScriptCompiler.Compile(bottom).HasErrors);
    }

    [EngineFact]
    public void PlaceOnDeck_ThisCard_GoesOnTop()
    {
        var monster = new CardProject
        {
            Card = Monster(100171044),
            Effects = [new EffectEvent { When = B("when.ignition"), Do = [With(B(PlaceOnDeckAction.BlockId), "cards", new CardFilter { ThisCard = true })] }],
        };
        new EngineScenario(monster).Card(monster.Card.Id, 0, "mzone").Card(Dragon, 0, "deck", 4)
            .Activate(monster.Card.Id).ExpectActivations(1)
            .Check($"Duel.GetDecktopGroup(0,1):GetFirst():IsCode({monster.Card.Id})").AssertPasses();
        Assert.Equal("You can place this card on top of the Deck.", EffectTextGenerator.Generate(monster));
    }

    [EngineFact]
    public void PlaceOnDeck_GishkiAvance_Translated()
    {
        if (Script(16693254) is null) return;
        // "Once per turn: You can choose 1 "Gishki" monster from your Deck and place it on top of your Deck."
        var card = Monster(16693254, CardRace.Aqua, 1500);
        card.Archetypes = [0x3a];
        var p = Applied(16693254, card);
        var gishki = new CardProject { Card = new CardData { Id = 100171046, Name = "Gishki Filler", Frame = MonsterFrame.Normal, Level = 4, Atk = 1000, Def = 1000, Archetypes = [0x3a] } };
        new EngineScenario(p, gishki).Card(card.Id, 0, "mzone").Card(gishki.Card.Id, 0, "deck").Card(Dragon, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"Duel.GetDecktopGroup(0,1):GetFirst():IsCode({gishki.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void PlaceOnDeck_HelpingRoboForCombat_Translated()
    {
        if (Script(47025270) is null) return;
        // "Draw 1 card, then return 1 card from your hand to the bottom of the Deck."
        var card = Monster(47025270, CardRace.Machine, 1600);
        var p = Applied(47025270, card);
        Assert.Contains(p.Effects.SelectMany(e => e.Do), b => b.BlockId == PlaceOnDeckAction.BlockId && b.Values["where"] == "bottom");
        new EngineScenario(p).Card(card.Id, 1, "mzone").Card(Fiend, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Dragon, 1, "deck", 4)
            .Attack(card.Id).Activate(card.Id).Turns(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0")
            .Check($"Duel.GetFieldGroup(1,LOCATION_DECK,0):Filter(function(c) return c:GetSequence()==0 end,nil):GetFirst():IsCode({Dragon})")
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- banished face-down

    [EngineFact]
    public void Banish_FaceDown()
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = 100171050, Name = "Face-down Exile", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [With(B("do.banish", ("facedown", "true"), ("target", "true")), "cards", CardFilter.OpponentField())] }],
        };
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check("Duel.IsExistingMatchingCard(Card.IsFacedown,1,LOCATION_REMOVED,0,1,nil)").AssertPasses();
        Assert.Equal("Target 1 card your opponent controls; banish it face-down.", EffectTextGenerator.Generate(spell));
        Assert.Contains("c:IsAbleToRemove(tp,POS_FACEDOWN)", ScriptCompiler.Compile(spell).Lua);
        // Face-down cards don't come back in the End Phase.
        spell.Effects[0].Do[0].Values["until"] = "end_phase";
        Assert.True(ScriptCompiler.Compile(spell).HasErrors);
    }

    // ---------------------------------------------------------------- battle positions

    [EngineFact]
    public void Position_AvengingKnightParshath_TogglesAFaceUpMonster()
    {
        if (Script(69514125) is null) return;
        // "Duel.ChangePosition(tc,POS_FACEUP_DEFENSE,0,POS_FACEUP_ATTACK,0)" on a face-up monster: the position block's "other position".
        var card = Monster(69514125, CardRace.Fairy, 1900);
        card.Frame = MonsterFrame.Synchro;
        card.Level = 5;
        var p = Applied(69514125, card);
        Assert.Contains(p.Effects.SelectMany(e => e.Do), b => b.BlockId == "do.change_position" && b.Values["to"] == "toggle");
        new EngineScenario(p).Card(card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"{Find(Warrior, 1, "LOCATION_MZONE")}:IsPosition(POS_FACEUP_DEFENSE)").AssertPasses();
    }

    [EngineFact]
    public void Position_WormErokin_SetsAFaceUpMonster()
    {
        if (Script(14936691) is null) return;
        // "Duel.ChangePosition(tc,POS_FACEDOWN_DEFENSE,0,POS_FACEDOWN_DEFENSE,0)": the block's "to face-down Defense Position".
        var card = Monster(14936691, CardRace.Reptile, 1400);
        card.Archetypes = [0x3e];
        var p = Applied(14936691, card);
        Assert.Contains(p.Effects.SelectMany(e => e.Do), b => b.BlockId == "do.change_position" && b.Values["to"] == "facedown");
        new EngineScenario(p).Card(card.Id, 0, "mzone", 2).Card(Warrior, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check("Duel.IsExistingMatchingCard(Card.IsPosition,0,LOCATION_MZONE,0,1,nil,POS_FACEDOWN_DEFENSE)").AssertPasses();
    }

    [Fact]
    public void Position_AnyMonster_ChangedToDefense_StaysScript()
    {
        // "Change 1 face-up monster to Defense Position": a face-up Defense Position monster can be picked and stays as it
        // is, while the block only picks monsters it can change. The translation must not claim it.
        const string lua = """
            local s,id=GetID()
            function s.initial_effect(c)
            	local e1=Effect.CreateEffect(c)
            	e1:SetCategory(CATEGORY_POSITION)
            	e1:SetType(EFFECT_TYPE_ACTIVATE)
            	e1:SetCode(EVENT_FREE_CHAIN)
            	e1:SetProperty(EFFECT_FLAG_CARD_TARGET)
            	e1:SetTarget(s.target)
            	e1:SetOperation(s.activate)
            	c:RegisterEffect(e1)
            end
            function s.filter(c)
            	return c:IsFaceup() and c:IsCanChangePosition()
            end
            function s.target(e,tp,eg,ep,ev,re,r,rp,chk,chkc)
            	if chkc then return chkc:IsLocation(LOCATION_MZONE) and s.filter(chkc) end
            	if chk==0 then return Duel.IsExistingTarget(s.filter,tp,LOCATION_MZONE,LOCATION_MZONE,1,nil) end
            	Duel.SelectTarget(tp,s.filter,tp,LOCATION_MZONE,LOCATION_MZONE,1,1,nil)
            end
            function s.activate(e,tp,eg,ep,ev,re,r,rp)
            	local tc=Duel.GetFirstTarget()
            	if tc:IsRelateToEffect(e) then
            		Duel.ChangePosition(tc,POS_FACEUP_DEFENSE)
            	end
            end
            """;
        var card = new CardData { Id = 100171062, Name = "T", Kind = CardKind.Spell };
        Assert.False(ScriptTranslator.Translate(lua, 100171062, card).Complete);
        // Attack Position monsters only: exactly the block's "to face-up Defense Position".
        var attackOnly = lua.Replace("c:IsFaceup() and c:IsCanChangePosition()", "c:IsAttackPos() and c:IsCanChangePosition()");
        var result = ScriptTranslator.Translate(attackOnly, 100171062, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        Assert.Equal("defense", result.Pieces.Single().Effect!.Do.Single().Values["to"]);
        // Without the check that the monster can change its position (Link Monsters can't), the block would pick fewer.
        Assert.False(ScriptTranslator.Translate(attackOnly.Replace(" and c:IsCanChangePosition()", ""), 100171062, card).Complete);
    }

    // ---------------------------------------------------------------- excavating

    private static CardProject Excavator(long id, string take, string rest, CardFilter cards) => new()
    {
        Card = new CardData { Id = id, Name = "Mechanics Excavator " + id, Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [With(B(ExcavateAction.BlockId, ("count", "5"), ("take", take), ("rest", rest)), "cards", cards)] }],
    };

    [EngineFact]
    public void Excavate_AddsOneToTheHand_AndSendsTheRestToTheGY()
    {
        var spell = Excavator(100171080, "add", "gy", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior });
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 2).Card(Dragon, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_HAND,0,nil,{Warrior})~=nil")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            // 5 excavated: 1 added, 4 sent to the GY (with the Spell itself), 1 left in the Deck.
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==5")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==1").AssertPasses();
        Assert.Equal("Excavate the top 5 cards of your Deck, and if you do, you can add 1 excavated Warrior monster to your hand, also send the rest to the GY.",
            EffectTextGenerator.Generate(spell));
    }

    [EngineFact]
    public void Excavate_SpecialSummonsOne_AndPlacesTheRestOnTheBottom()
    {
        var spell = Excavator(100171081, "special", "bottom", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior });
        // A Fiend sits under the five excavated cards: after the rest go to the bottom, it is on top.
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Fiend, 0, "deck").Card(Warrior, 0, "deck", 2).Card(Dragon, 0, "deck", 3)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior})~=nil")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==5")
            .Check($"Duel.GetDecktopGroup(0,1):GetFirst():IsCode({Fiend})").AssertPasses();
        Assert.EndsWith("you can Special Summon 1 excavated Warrior monster, also place the rest on the bottom of the Deck in any order.", EffectTextGenerator.Generate(spell));
    }

    [EngineFact]
    public void Excavate_WithNothingToTake_ShufflesTheRestBack()
    {
        var spell = Excavator(100171082, "add", "shuffle", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Fiend });
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 6)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==6").AssertPasses();
    }

    // ---------------------------------------------------------------- equipping monsters

    private static CardProject Equipper(long id, CardFilter cards, bool target = false) => new()
    {
        Card = Monster(id, CardRace.Insect, 1000),
        Effects = [new EffectEvent { When = B("when.ignition"), Do = [With(B(EquipToThisAction.BlockId, ("target", target ? "true" : "false")), "cards", cards)] }],
    };

    [EngineFact]
    public void EquipToThis_AMonsterFromTheGY_BecomesItsEquipCard()
    {
        var bug = Equipper(100171090, new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster });
        var eq = Find(Warrior, 0, "LOCATION_SZONE");
        new EngineScenario(bug).Card(bug.Card.Id, 0, "mzone").Card(Warrior, 0, "grave").Card(Warrior, 0, "deck", 4)
            .Activate(bug.Card.Id).ExpectActivations(1)
            .Check($"{eq}:GetEquipTarget():IsCode({bug.Card.Id})")
            // Only this card can hold it: another monster can't take it.
            .Check($"not {eq}:CheckEquipTarget(Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_DECK,0,nil,{Warrior}))")
            .Check($"{eq}:CheckEquipTarget({Find(bug.Card.Id, 0, "LOCATION_MZONE")})").AssertPasses();
        Assert.Equal("You can equip 1 monster from your GY to this card.", EffectTextGenerator.Generate(bug));
    }

    [EngineFact]
    public void EquipToThis_ATargetYourOpponentControls()
    {
        var bug = Equipper(100171091, new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }, target: true);
        new EngineScenario(bug).Card(bug.Card.Id, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(bug.Card.Id).ExpectActivations(1)
            .Check($"{Find(Dragon, 0, "LOCATION_SZONE")}:GetEquipTarget():IsCode({bug.Card.Id})")
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0").AssertPasses();
        Assert.Equal("You can target 1 face-up monster your opponent controls; equip it to this card.", EffectTextGenerator.Generate(bug));
    }

    [EngineFact]
    public void EquipToThis_InzektorEarwig_Translated()
    {
        if (Script(38450736) is null) return;
        // "Once per turn: You can equip 1 "Inzektor" monster from your hand or GY to this card."
        var card = Monster(38450736, CardRace.Insect, 1000);
        card.Archetypes = [0x56];
        var result = ScriptTranslator.Translate(Script(38450736)!, 38450736, card);
        var equip = result.Pieces.SelectMany(p => p.Effects).Single(e => e.Do.Any(b => b.BlockId == EquipToThisAction.BlockId));
        Assert.Equal(Zone.Hand | Zone.Graveyard, equip.Do.Single().Filters["cards"].Zones);
        var project = new CardProject { Card = card, Effects = [equip] };
        var inzektor = new CardProject { Card = new CardData { Id = 100171092, Name = "Inzektor Filler", Frame = MonsterFrame.Normal, Level = 3, Atk = 500, Def = 500, Race = CardRace.Insect, Archetypes = [0x56] } };
        new EngineScenario(project, inzektor).Card(card.Id, 0, "mzone").Card(inzektor.Card.Id, 0, "grave").Card(Warrior, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"{Find(inzektor.Card.Id, 0, "LOCATION_SZONE")}:GetEquipTarget():IsCode({card.Id})").AssertPasses();
    }

    // ---------------------------------------------------------------- the Chain

    [EngineFact]
    public void CannotBeNegated_TheOpponentsNegationDoesNothing()
    {
        var pot = Samples.PotOfGreedClone();
        pot.Effects[0].Cost.Add(B(CannotBeNegatedCost.BlockId));
        var negator = new CardProject
        {
            Card = new CardData { Id = 100171100, Name = "Hand Negator", Level = 3, Atk = 0, Def = 1800 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.quick").With("timing", "chain_any").With("zone", "hand"),
                    Cost = [new BlockInstance("cost.this_card").With("how", "discard")],
                    Do = [new BlockInstance("do.negate").With("what", "activation")],
                },
            ],
        };
        new EngineScenario(pot, negator)
            .Card(negator.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Card(pot.Card.Id, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Activate(negator.Card.Id).Activate(pot.Card.Id).Turns(2)
            .ExpectActivations(2)
            // The negation resolves but can't negate it: the opponent drew 1 for turn and 2 from the Spell.
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==3").AssertPasses();
        Assert.EndsWith("This card's activation and effect cannot be negated.", EffectTextGenerator.Generate(pot));
        var lua = ScriptCompiler.Compile(pot).Lua;
        Assert.Contains("EFFECT_FLAG_CANNOT_INACTIVATE+EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_CAN_FORBIDDEN", lua);
        Assert.Contains("SetCode(EFFECT_CANNOT_DISABLE)", lua);
    }

    [EngineFact]
    public void CannotBeNegated_Spiritualism_Translated()
    {
        if (Script(15866454) is null) return;
        var card = new CardData { Id = 15866454, Name = "Spiritualism", Kind = CardKind.Spell };
        var p = Applied(15866454, card);
        Assert.Contains(p.Effects.Single().Cost, b => b.BlockId == CannotBeNegatedCost.BlockId);
        new EngineScenario(p).Card(card.Id, 0, "hand").Card(Spell, 1, "szone").Card(Warrior, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==1").AssertPasses();
    }

    [Fact]
    public void ChainLimit_FromTheTargetStep_Translated()
    {
        if (Script(19684740) is null) return;
        // "Duel.SetChainLimit(c19684740.chlimit)" with "return tp==ep": your opponent cannot respond.
        var card = Monster(19684740, CardRace.Fairy, 2600);
        card.Frame = MonsterFrame.Xyz;
        var effect = ScriptTranslator.Translate(Script(19684740)!, 19684740, card).Pieces.SelectMany(p => p.Effects).Single(e => e.Cost.Any(b => b.BlockId == "cost.chain_limit"));
        var limit = effect.Cost.Single(b => b.BlockId == "cost.chain_limit");
        Assert.Equal(("opponent", "any"), (limit.Values["who"], limit.Values["what"]));
        // "Duel.SetChainLimit(aux.FALSE)": neither player.
        const string lua = "local s,id=GetID()\nfunction s.initial_effect(c)\n\tlocal e1=Effect.CreateEffect(c)\n\te1:SetCategory(CATEGORY_DRAW)\n\te1:SetType(EFFECT_TYPE_ACTIVATE)\n\te1:SetCode(EVENT_FREE_CHAIN)\n" +
                           "\te1:SetTarget(s.target)\n\te1:SetOperation(s.activate)\n\tc:RegisterEffect(e1)\nend\nfunction s.target(e,tp,eg,ep,ev,re,r,rp,chk)\n\tif chk==0 then return Duel.IsPlayerCanDraw(tp,1) end\n" +
                           "\tDuel.SetChainLimit(aux.FALSE)\nend\nfunction s.activate(e,tp,eg,ep,ev,re,r,rp)\n\tDuel.Draw(tp,1,REASON_EFFECT)\nend\n";
        var spell = ScriptTranslator.Translate(lua, 100171101, new CardData { Id = 100171101, Name = "T", Kind = CardKind.Spell });
        Assert.True(spell.Complete, string.Join("\n", ScriptTranslator.Untranslated(spell)));
        Assert.Equal(("both", "any"), (spell.Pieces.Single().Effect!.Cost.Single().Values["who"], spell.Pieces.Single().Effect!.Cost.Single().Values["what"]));
    }

    [EngineFact]
    public void SortDeckTop_LooksAndPutsThemBack()
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = 100171083, Name = "Mechanics Sort", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B(SortDeckTopAction.BlockId, ("count", "3"))] }],
        };
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==4").AssertPasses();
        Assert.Equal("Look at the top 3 cards of your Deck, then place them on top of the Deck in any order.", EffectTextGenerator.Generate(spell));
        Assert.Contains("Duel.SortDecktop(tp,tp,3)", ScriptCompiler.Compile(spell).Lua);
        if (Script(49998907) is not { } lua) return;
        var translated = ScriptTranslator.Translate(lua, 49998907, new CardData { Id = 49998907, Name = "T", Kind = CardKind.Spell });
        Assert.True(translated.Complete, string.Join("\n", ScriptTranslator.Untranslated(translated)));
        Assert.Equal("3", translated.Pieces.Single().Effect!.Do.Single().Values["count"]);
    }

    // ---------------------------------------------------------------- control

    [EngineFact]
    public void SwapControl_BothMonstersChangeSides()
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = 100171130, Name = "Mechanics Swap", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B(SwapControlAction.BlockId)] }],
        };
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check($"{Find(Dragon, 0, "LOCATION_MZONE")}~=nil")
            .Check($"{Find(Warrior, 1, "LOCATION_MZONE")}~=nil").AssertPasses();
        Assert.Equal("Target 1 monster you control and 1 monster your opponent controls; switch control of both monsters.", EffectTextGenerator.Generate(spell));
        // Nothing of the opponent's to take: it can't be activated.
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void SwapControl_TG1EM1_Translated()
    {
        if (Script(76641981) is null) return;
        // It selects the opponent's monster first and merges the other way round: the same switch.
        var card = new CardData { Id = 76641981, Name = "TG1-EM1", Kind = CardKind.Spell };
        var p = Applied(76641981, card);
        var swap = p.Effects.Single().Do.Single();
        Assert.Equal(0x27, swap.Filters["mine"].Archetype);
        Assert.True(swap.Filters["mine"].FaceUpOnly);
        var tg = new CardProject { Card = new CardData { Id = 100171131, Name = "T.G. Filler", Frame = MonsterFrame.Normal, Level = 4, Atk = 1000, Def = 1000, Archetypes = [0x27] } };
        new EngineScenario(p, tg).Card(card.Id, 0, "hand").Card(tg.Card.Id, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"{Find(tg.Card.Id, 1, "LOCATION_MZONE")}~=nil and {Find(Dragon, 0, "LOCATION_MZONE")}~=nil").AssertPasses();
    }

    // ---------------------------------------------------------------- the End Phase

    private static CardProject Reviver(long id, string how) => new()
    {
        Card = new CardData { Id = id, Name = "Mechanics Reviver " + id, Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B("do.special_summon"), B(EndPhaseAction.BlockId, ("how", how)).Linked(ActionLink.IfYouDo)],
            },
        ],
    };

    [EngineFact]
    public void EndPhase_TheRevivedMonsterIsDestroyed()
    {
        var spell = Reviver(100171140, "destroy");
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "grave").Card(Warrior, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{Warrior})").AssertPasses();
        Assert.Equal("Special Summon 1 monster from your GY, but destroy it during the End Phase.", EffectTextGenerator.Generate(spell));
    }

    [EngineFact]
    public void EndPhase_ReturnsItToTheHand_AndNeedsAStepBefore()
    {
        var spell = Reviver(100171141, "hand");
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "grave").Card(Warrior, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{Warrior})").AssertPasses();
        Assert.EndsWith(", but return it to the hand during the End Phase.", EffectTextGenerator.Generate(spell));
        spell.Effects[0].Do.RemoveAt(0);
        Assert.True(ScriptCompiler.Compile(spell).HasErrors);
    }

    [EngineFact]
    public void EndPhase_StormDragonsReturn_Translated()
    {
        if (Script(83195035) is null) return;
        // "Target 1 banished monster; Special Summon it to your field, but return it to the hand during the End Phase."
        var card = new CardData { Id = 83195035, Name = "Storm Dragon's Return", Kind = CardKind.Spell };
        var p = Applied(83195035, card);
        Assert.Contains(p.Effects.Single().Do, b => b.BlockId == EndPhaseAction.BlockId && b.Values["how"] == "hand");
        new EngineScenario(p).Card(card.Id, 0, "hand").Card(Dragon, 0, "removed").Card(Warrior, 0, "deck", 4)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{Dragon})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0").AssertPasses();
    }

    // ---------------------------------------------------------------- Types and Attributes

    [EngineFact]
    public void Become_AllFaceUpMonstersOnTheField_BecomeDark()
    {
        var lord = new CardProject
        {
            Card = Monster(100171110),
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [B(BecomePassive.BlockId, ("what", "attribute"), ("attribute", "Dark"))] }],
        };
        new EngineScenario(lord).Card(lord.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Check($"{Find(Dragon, 1, "LOCATION_MZONE")}:IsAttribute(ATTRIBUTE_DARK) and not {Find(Dragon, 1, "LOCATION_MZONE")}:IsAttribute(ATTRIBUTE_LIGHT)")
            .Check($"{Find(Warrior, 0, "LOCATION_MZONE")}:IsAttribute(ATTRIBUTE_DARK)").AssertPasses();
        Assert.Equal("All face-up monsters on the field become DARK.", EffectTextGenerator.Generate(lord));
        // Only where the monsters are: no target check, as official cards write it.
        Assert.DoesNotContain("SetTarget(", ScriptCompiler.Compile(lord).Lua);
    }

    [EngineFact]
    public void Become_YourOpponentsDragons_BecomeZombies()
    {
        var lord = new CardProject
        {
            Card = Monster(100171111),
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.while_face_up"),
                    Do = [With(B(BecomePassive.BlockId, ("what", "race"), ("race", "Zombie")), "cards",
                        new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Dragon })],
                },
            ],
        };
        new EngineScenario(lord).Card(lord.Card.Id, 0, "mzone").Card(Dragon, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Check($"{Find(Dragon, 1, "LOCATION_MZONE")}:IsRace(RACE_ZOMBIE)")
            .Check($"{Find(Dragon, 0, "LOCATION_MZONE")}:IsRace(RACE_DRAGON)")
            .Check($"{Find(Warrior, 1, "LOCATION_MZONE")}:IsRace(RACE_WARRIOR)").AssertPasses();
        Assert.Equal("All face-up Dragon monsters your opponent controls become Zombie monsters.", EffectTextGenerator.Generate(lord));
    }

    [EngineFact]
    public void Become_KitsuniKitsunebi_Translated()
    {
        if (Script(3361010) is null) return;
        var card = Monster(3361010, CardRace.Pyro, 1000);
        var effect = ScriptTranslator.Translate(Script(3361010)!, 3361010, card).Pieces.SelectMany(p => p.Effects).Single(e => e.Do.Any(b => b.BlockId == BecomePassive.BlockId));
        var p = new CardProject { Card = card, Effects = [effect] };
        Assert.Equal("All face-up monsters on the field become FIRE.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Check($"{Find(Warrior, 1, "LOCATION_MZONE")}:IsAttribute(ATTRIBUTE_FIRE)").AssertPasses();
    }

    // ---------------------------------------------------------------- both players

    [EngineFact]
    public void DamageBoth_BothPlayersTakeIt()
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = 100171120, Name = "Mutual Burn", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B(DamageBothAction.BlockId, ("amount", "700"))] }],
        };
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check("Duel.GetLP(0)==7300 and Duel.GetLP(1)==7300").AssertPasses();
        Assert.Equal("Inflict 700 damage to both players.", EffectTextGenerator.Generate(spell));
    }

    [EngineFact]
    public void DamageBoth_Abaki_Translated()
    {
        if (Script(12694768) is null) return;
        // "If this card is destroyed by battle and sent to the GY: Inflict 500 damage to both players."
        var card = Monster(12694768, CardRace.Fiend, 1000);
        var p = Applied(12694768, card);
        Assert.Contains(p.Effects.SelectMany(e => e.Do), b => b.BlockId == DamageBothAction.BlockId && b.Values["amount"] == "500");
        new EngineScenario(p).Card(card.Id, 0, "mzone").Card(EffectFiller, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(EffectFiller).Activate(card.Id).Turns(2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{card.Id})")
            // 400 battle damage, then 500 each.
            .Check("Duel.GetLP(0)==7100 and Duel.GetLP(1)==7500").AssertPasses();
    }

    [EngineFact]
    public void SpecialSummonThisCard_WrittenWithGetHandler_FabledLurrie()
    {
        if (Script(97651498) is null) return;
        // "If this card is discarded to the GY: Special Summon it." with Duel.SpecialSummon(e:GetHandler(),...).
        var p = Applied(97651498, Monster(97651498, CardRace.Fiend, 200));
        Assert.Contains(p.Effects.SelectMany(e => e.Do), b => b.BlockId == "do.special_summon_self");
    }
}
