using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The cards of the summon-history, duel-history, trigger-detail, activation-check and limit blocks, built from blocks:
/// the parts of Cooky☆Yummy, Kashtira Riseheart, Invoked Magistus Heruvim, the Sun God cards, Heart of the Blue-Eyes,
/// Triple Tactics Talent, Number 89, Kashtira Preparations, Nibiru, Invoked Caliga, Droll &amp; Lock Bird and the rest.
/// </summary>
internal static class ConditionCards
{
    public static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static BlockInstance Draw(int n = 1) => B("do.draw").With("count", n);
    private static BlockInstance Gain(int lp) => B("do.gain_lp").With("amount", lp);

    private static CardData Monster(long id, string name, CardAttribute attr = CardAttribute.Dark, CardRace race = CardRace.Fiend, int level = 4, int atk = 1000) =>
        new() { Id = id, Name = name, Frame = MonsterFrame.Effect, Level = level, Atk = atk, Def = 1000, Race = race, Attribute = attr };

    public const long YummyId = 100320001, RiseheartId = 100320002, HeruvimId = 100320003, GilGarthId = 100320004, ImmortalId = 100320005,
        LeadingId = 100320006, HeartId = 100320007, AnkhId = 100320008, TacticsId = 100320009, DiablosisId = 100320010, PreparationsId = 100320011,
        NibiruId = 100320012, CaligaId = 100320013, DrollId = 100320014, ReindsurmId = 100320015, GearBoxId = 100320016, GeniusId = 100320017,
        CounterSpellId = 100320018, SebekId = 100320019, RollerId = 100320020, ToddlerId = 100320021, JesterId = 100320022, LagoonId = 100320023,
        AshId = 100320024, VirakamId = 100320025, MechabaId = 100320026, ToonWorldId = 100320027, BaronneId = 100320028, VoloId = 100320029,
        MimighoulId = 100320030, KarinId = 100320031, AiwassId = 100320032;

    /// <summary>Cooky☆Yummy's check: "if this card was Special Summoned by the effect of a Synchro Monster".</summary>
    public static CardProject Yummy() => new()
    {
        Card = Monster(YummyId, "Cooky Yummy", CardAttribute.Light, CardRace.Beast, 2, 600),
        Effects =
        [
            new EffectEvent
            {
                Label = "Treat", When = B("when.summoned").With("how", "special"), Limit = OncePerTurn.Hard,
                If = [B("if.summoned_by").With("how", "by_synchro_effect")],
                Do = [Draw()],
            },
        ],
    };

    /// <summary>"During your Main Phase, if this card was Normal or Special Summoned this turn: You can ...".</summary>
    public static CardProject Riseheart() => new()
    {
        Card = Monster(RiseheartId, "Kashtira Riseheart", level: 3, atk: 1500),
        Effects =
        [
            new EffectEvent
            {
                Label = "Banish", When = B("when.ignition"), Limit = OncePerTurn.Hard,
                If = [B("if.summoned_this_turn").With("how", "normal_special")],
                Do = [Draw()],
            },
        ],
    };

    /// <summary>"When your opponent activates a monster effect, or their monster declares an attack, except during the turn this card was Special Summoned (Quick Effect)".</summary>
    public static CardProject Heruvim() => new()
    {
        Card = Monster(HeruvimId, "Invoked Magistus Heruvim", CardAttribute.Dark, CardRace.Spellcaster, 6, 2500),
        Effects =
        [
            new EffectEvent
            {
                Label = "Swap", Limit = OncePerTurn.Hard,
                When = B(ActivationTrigger.BlockId).With("what", "monster"),
                AlsoWhen = [B(AttackDeclaredTrigger.BlockId)],
                If = [B("if.summoned_this_turn").With("how", "special").With("was", "not")],
                Cost = [B("cost.this_card").With("how", "banish")],
                Do = [Gain(1000)],
            },
        ],
    };

    /// <summary>"During the End Phase, if "The Immortal Sun God" was sent to your GY this turn: You can add this card from your GY to your hand."</summary>
    public static CardProject GilGarth() => new()
    {
        Card = Monster(GilGarthId, "Gil Garth, Exterminator of the Sun God", CardAttribute.Dark, CardRace.Machine, 6, 1800),
        Effects =
        [
            new EffectEvent
            {
                Label = "Return", When = B("when.phase").With("whose", "each").With("phase", "end").With("zone", "graveyard"), Limit = OncePerTurn.Hard,
                If = [B(CardHistoryCondition.BlockId).With("card", new CardFilter { CardCode = ImmortalId, Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Any })],
                Do = [B("do.add_to_hand").With("cards", new CardFilter { ThisCard = true })],
            },
        ],
    };

    /// <summary>"The Immortal Sun God", and the Spell that sends it from the Deck to the GY.</summary>
    public static CardProject Immortal() => new() { Card = new CardData { Id = ImmortalId, Name = "The Immortal Sun God", Kind = CardKind.Spell } };

    public const long SenderId = 100320040;

    public static CardProject Sender() => new()
    {
        Card = new CardData { Id = SenderId, Name = "Sun God Sender", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B("do.send_to_gy").With("cards", new CardFilter { CardCode = ImmortalId, Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Any })],
            },
        ],
    };

    /// <summary>"During your Main Phase, if this card was activated this turn: You can add 1 "Sun God" monster from your Deck to your hand."</summary>
    public static CardProject Leading() => new()
    {
        Card = new CardData { Id = LeadingId, Name = "The Sun God Leading Down into Darkness", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
        Effects =
        [
            new EffectEvent
            {
                Label = "Search", When = B("when.ignition"), Limit = OncePerTurn.Hard,
                If = [B(ActivatedThisTurnCondition.BlockId)],
                Do = [Draw()],
            },
        ],
    };

    /// <summary>Heart of the Blue-Eyes' check: "you have successfully activated "Millennium Ankh" this Duel".</summary>
    public static CardProject Heart() => new()
    {
        Card = Monster(HeartId, "Heart of the Blue-Eyes", CardAttribute.Light, CardRace.Dragon, 1, 0),
        Effects =
        [
            new EffectEvent
            {
                Label = "Return", When = B("when.ignition").With("zone", "hand"), Limit = OncePerTurn.Duel,
                If = [B(CardHistoryCondition.BlockId).With("event", "activated").With("when", "duel")
                    .With("card", new CardFilter { CardCode = AnkhId, Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Any })],
                Do = [Draw()],
            },
        ],
    };

    public static CardProject Ankh() => new()
    {
        Card = new CardData { Id = AnkhId, Name = "Millennium Ankh", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [Gain(100)] }],
    };

    /// <summary>"If your opponent has activated a monster effect during your Main Phase this turn: ..."</summary>
    public static CardProject Tactics() => new()
    {
        Card = new CardData { Id = TacticsId, Name = "Triple Tactics Talent", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"), Limit = OncePerTurn.Hard,
                If = [B("if.opponent_activated").With("what", "monster").With("during", "your_main")],
                Do = [Draw(2)],
            },
        ],
    };

    /// <summary>"At the end of the Battle Phase, if this card destroyed a monster by battle: You can ..."</summary>
    public static CardProject Diablosis() => new()
    {
        Card = Monster(DiablosisId, "Number 89: Diablosis the Mind Hacker", CardAttribute.Dark, CardRace.Psychic, 4, 2800),
        Effects =
        [
            new EffectEvent
            {
                Label = "Banish", When = B("when.phase").With("whose", "each").With("phase", "battle_end"), Limit = OncePerTurn.Soft,
                If = [B("if.battled_this_turn").With("what", "destroyed_bp")],
                Do = [Gain(500)],
            },
        ],
    };

    /// <summary>"If your opponent activates a Trap Card or effect, ... and this card is already face-up in your Spell &amp; Trap Zone: ..."</summary>
    public static CardProject Preparations() => new()
    {
        Card = new CardData { Id = PreparationsId, Name = "Kashtira Preparations", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
        Effects =
        [
            new EffectEvent
            {
                Label = "Look", Limit = OncePerTurn.Hard,
                When = B("when.event").With("event", "opp_spell_trap_effect"),
                If = [B(AlreadyFaceUpCondition.BlockId)],
                Do = [Draw()],
            },
        ],
    };

    /// <summary>"During the Main Phase, if your opponent Normal or Special Summoned 5 or more monsters this turn (Quick Effect): ..."</summary>
    public static CardProject Nibiru(int count = 5, string counting = "cards") => new()
    {
        Card = Monster(NibiruId, "Nibiru, the Primal Being", CardAttribute.Light, CardRace.Rock, 11, 3000),
        Effects =
        [
            new EffectEvent
            {
                Label = "Primal", When = B("when.quick").With("zone", "hand"), Limit = OncePerTurn.Hard,
                If =
                [
                    B("if.phase").With("phase", "main"),
                    B(EventThisTurnCondition.BlockId).With("event", "opp_normal_special").With("count", count).With("counting", counting),
                ],
                Do = [Gain(1000)],
            },
        ],
    };

    /// <summary>Invoked Caliga, for each player: "can only attempt to activate 1 monster effect per turn", counted while it is face-up.</summary>
    public static CardProject Caliga() => new()
    {
        Card = Monster(CaligaId, "Invoked Caliga", CardAttribute.Dark, CardRace.Fiend, 8, 2400),
        Effects =
        [
            new EffectEvent
            {
                Label = "Your lock", When = B("when.while_face_up"),
                If = [B(EventThisTurnCondition.BlockId).With("event", "you_monster_effect").With("watch", "face_up")],
                Do = [B("passive.lock").With("who", "you").With("what", "activate_monster")],
            },
            new EffectEvent
            {
                Label = "Their lock", When = B("when.while_face_up"),
                If = [B(EventThisTurnCondition.BlockId).With("event", "opp_monster_effect").With("watch", "face_up")],
                Do = [B("passive.lock").With("who", "opponent").With("what", "activate_monster")],
            },
        ],
    };

    /// <summary>"If a card(s) is added from the Main Deck to your opponent's hand, except during the Draw Phase (Quick Effect): ..."</summary>
    public static CardProject Droll() => new()
    {
        Card = Monster(DrollId, "Droll & Lock Bird", CardAttribute.Wind, CardRace.Spellcaster, 1, 0),
        Effects =
        [
            new EffectEvent
            {
                Label = "Lock",
                When = B(CardsDetailTrigger.BlockId).With("event", "to_hand").With("from", "deck").With("speed", "quick").With("zone", "hand")
                    .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand, CardType = FilterCardType.Any }),
                If = [B("if.phase").With("phase", "draw").With("is", "not")],
                Cost = [B("cost.this_card").With("how", "grave")],
                Do = [Gain(1000)],
            },
        ],
    };

    /// <summary>"If this card is sent to the GY, except by battle: You can ..."</summary>
    public static CardProject Reindsurm() => new()
    {
        Card = Monster(ReindsurmId, "Reindsurm Amurusa", CardAttribute.Wind, CardRace.Insect, 8, 2800),
        Effects =
        [
            new EffectEvent
            {
                Label = "Discard", When = B("when.ignition").With("zone", "hand"), Cost = [B("cost.this_card").With("how", "discard")], Do = [Gain(100)],
            },
            new EffectEvent
            {
                Label = "Ritual", When = B(ThisCardDetailTrigger.BlockId).With("event", "sent_gy").With("by", "not_battle"), Limit = OncePerTurn.Hard,
                Do = [Gain(1000)],
            },
        ],
    };

    /// <summary>"If this card is added from the Deck or GY to your hand, except by drawing it: You can ..."</summary>
    public static CardProject GearBox() => new()
    {
        Card = Monster(GearBoxId, "Ancient Gear Box", CardAttribute.Earth, CardRace.Machine, 1, 500),
        Effects =
        [
            new EffectEvent
            {
                Label = "Search", When = B(ThisCardDetailTrigger.BlockId).With("event", "to_hand").With("from", "deck_gy").With("by", "not_draw"),
                Limit = OncePerTurn.Hard, Do = [Gain(1000)],
            },
        ],
    };

    public const long SearcherId = 100320041;

    /// <summary>A Spell that adds a card from the Deck to the hand.</summary>
    public static CardProject Searcher(long code) => new()
    {
        Card = new CardData { Id = SearcherId, Name = "Searcher", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B("do.add_to_hand").With("cards", new CardFilter { CardCode = code, Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Any })],
            },
        ],
    };

    /// <summary>"When 2 monsters are Special Summoned at the same time to the zones this card points to: You can ..."</summary>
    public static CardProject Genius() => new()
    {
        Card = new CardData
        {
            Id = GeniusId, Name = "Qliphort Genius", Frame = MonsterFrame.Link, Race = CardRace.Machine, Attribute = CardAttribute.Earth, Atk = 1800, Level = 2,
            LinkMarkers = LinkMarker.Bottom | LinkMarker.Top,
        },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link },
        Effects =
        [
            new EffectEvent
            {
                Label = "Search", When = B(CardsDetailTrigger.BlockId).With("count", "two").With("wording", "when")
                    .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, PointedToByThisCard = true }),
                Do = [Gain(1000)],
            },
        ],
    };

    /// <summary>"When you draw for your normal draw in your Draw Phase: You can banish this card from your GY; ..."</summary>
    public static CardProject CounterSpell() => new()
    {
        Card = new CardData { Id = CounterSpellId, Name = "Counter Spell \"Negate Attack\"", Kind = CardKind.Spell, SpellKind = SpellKind.QuickPlay },
        Effects =
        [
            new EffectEvent
            {
                Label = "Summon", When = B("when.event").With("event", "you_normal_draw").With("wording", "when").With("zone", "graveyard"),
                Cost = [B("cost.this_card").With("how", "banish")],
                Do = [Gain(1000)],
            },
        ],
    };

    /// <summary>
    /// "When this card or your monster this card points to inflicts battle damage to your opponent: You can ..." and
    /// "If a player gains LP, even during the Damage Step: You can ...".
    /// </summary>
    public static CardProject Sebek() => new()
    {
        Card = new CardData
        {
            Id = SebekId, Name = "Sorcerer of Sebek", Frame = MonsterFrame.Link, Race = CardRace.Spellcaster, Attribute = CardAttribute.Dark, Atk = 1900, Level = 2,
            LinkMarkers = LinkMarker.Top | LinkMarker.Bottom,
        },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link },
        Effects =
        [
            new EffectEvent
            {
                Label = "Gain", Limit = OncePerTurn.Hard, When = B("when.this_card").With("event", "battle_damage_when"),
                AlsoWhen =
                [
                    B(CardsDetailTrigger.BlockId).With("event", "battle_damage").With("wording", "when")
                        .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, PointedToByThisCard = true }),
                ],
                Do = [Gain(500)],
            },
            new EffectEvent
            {
                Label = "Burn", Limit = OncePerTurn.Hard, When = B("when.event").With("event", "any_gain_lp").With("damage_step", "even"),
                Do = [B("do.damage").With("amount", 1000)],
            },
        ],
    };

    /// <summary>"If an EARTH monster is Tributed: You can Special Summon this card from the GY (if it was there when the monster was Tributed) ..."</summary>
    public static CardProject Roller() => new()
    {
        Card = Monster(RollerId, "Infinitrack Road Roller", CardAttribute.Earth, CardRace.Machine, 4, 1000),
        Effects =
        [
            new EffectEvent
            {
                Label = "Roll", Limit = OncePerTurn.Hard,
                When = B(CardsDetailTrigger.BlockId).With("event", "tributed").With("zone", "graveyard").With("already_gy", true).With("other", "not_this")
                    .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Attribute = CardAttribute.Earth }),
                Do = [B("do.special_summon").With("cards", new CardFilter { ThisCard = true })],
            },
        ],
    };

    public const long TributerId = 100320042;

    /// <summary>A Spell whose cost Tributes a monster.</summary>
    public static CardProject Tributer() => new()
    {
        Card = new CardData { Id = TributerId, Name = "Tributer", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Cost = [B("cost.tribute")], Do = [Draw()] }],
    };

    /// <summary>"Once per Chain, if a card or effect activation is negated (except during the Damage Step): You can ..."</summary>
    public static CardProject Toddler() => new()
    {
        Card = Monster(ToddlerId, "Tantrum Toddler", CardAttribute.Dark, CardRace.Fiend, 2, 0),
        Effects =
        [
            new EffectEvent
            {
                Label = "Attach", Limit = OncePerTurn.Chain, When = B("when.event").With("event", "activation_negated").With("damage_step", "except"),
                Do = [Gain(700)],
            },
        ],
    };

    /// <summary>"You can Special Summon this card (from your hand). If Summoned this way, during your opponent's next End Phase: ..."</summary>
    public static CardProject Jester() => new()
    {
        Card = Monster(JesterId, "Jester Confit", CardAttribute.Light, CardRace.Spellcaster, 1, 0),
        Effects =
        [
            new EffectEvent { Label = "Summon", When = B(SummonProcedureTrigger.BlockId) },
            new EffectEvent
            {
                Label = "Return", When = B("when.phase").With("whose", "opponent").With("phase", "end").With("after", "summoned_self").With("optional", false),
                Do = [B("do.return_to_hand").With("cards", new CardFilter { ThisCard = true })],
            },
        ],
    };

    /// <summary>"When this card is activated: You can add 1 WATER Cyberse monster from your Deck to your hand."</summary>
    public static CardProject Lagoon() => new()
    {
        Card = new CardData { Id = LagoonId, Name = "Marincess Reef Lagoon", Kind = CardKind.Spell, SpellKind = SpellKind.Field },
        Effects =
        [
            new EffectEvent
            {
                Label = "Activate", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do =
                [
                    B("do.add_to_hand").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Attribute = CardAttribute.Water, Race = CardRace.Cyberse })
                        .MayDo(),
                ],
            },
        ],
    };

    /// <summary>"When a card or effect is activated that includes any of these effects (Quick Effect): You can discard this card; negate that effect."</summary>
    public static CardProject Ash() => new()
    {
        Card = Monster(AshId, "Ash Blossom & Joyous Spring", CardAttribute.Fire, CardRace.Zombie, 3, 0),
        Effects =
        [
            new EffectEvent
            {
                Label = "Negate", Limit = OncePerTurn.Hard,
                When = B(ActivationTrigger.BlockId).With("who", "either").With("includes", "deck").With("zone", "hand"),
                Cost = [B("cost.this_card").With("how", "discard")],
                Do = [B("do.negate").With("what", "effect")],
            },
        ],
    };

    /// <summary>"When your opponent activates a card or effect that includes an effect that Special Summons a monster(s) from the Deck (Quick Effect)".</summary>
    public static CardProject Karin() => new()
    {
        Card = Monster(KarinId, "Karin the Celestial Sage", CardAttribute.Light, CardRace.Fairy, 7, 2500),
        Effects =
        [
            new EffectEvent
            {
                Label = "Negate", Limit = OncePerTurn.Hard, When = B(ActivationTrigger.BlockId).With("includes", "deck_special"),
                Do = [B("do.negate").With("what", "effect").With("and", "destroy")],
            },
        ],
    };

    /// <summary>"When your opponent activates a card or effect in response to your Fusion Monster's effect activation (Quick Effect)".</summary>
    public static CardProject Virakam() => new()
    {
        Card = Monster(VirakamId, "Virakam, the Divine Spirit of Artifice", CardAttribute.Light, CardRace.Spellcaster, 4, 1500),
        Effects =
        [
            new EffectEvent
            {
                Label = "Negate", Limit = OncePerTurn.Hard,
                When = B(ActivationTrigger.BlockId).With("responds", "yours")
                    .With("responds_card", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion, Zones = Zone.None }),
                Cost = [B("cost.this_card").With("how", "banish")],
                Do = [B("do.negate").With("what", "effect").With("and", "banish")],
            },
        ],
    };

    /// <summary>"Once per turn, when a Spell/Trap Card, or monster effect, is activated (Quick Effect): You can send the same type of card ... from your hand to the GY; ..."</summary>
    public static CardProject Mechaba() => new()
    {
        Card = Monster(MechabaId, "Invoked Mechaba", CardAttribute.Light, CardRace.Machine, 8, 2500),
        Effects =
        [
            new EffectEvent
            {
                Label = "Negate", Limit = OncePerTurn.Soft,
                When = B(ActivationTrigger.BlockId).With("who", "either").With("what", "card_or_monster"),
                Cost = [B("cost.move_cards").With("how", "grave").With("same_type", true).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Any })],
                Do = [B("do.negate").With("what", "activation").With("and", "banish")],
            },
        ],
    };

    /// <summary>"Once per turn: You can ... You can only use this effect of "X" thrice per turn."</summary>
    public static CardProject ToonWorld(int times = 3) => new()
    {
        Card = Monster(ToonWorldId, "Toon World the Perfect World", CardAttribute.Light, CardRace.Spellcaster, 4, 1000),
        Effects = [new EffectEvent { Label = "Search", When = B("when.ignition"), Limit = OncePerTurn.SoftAndHard, Times = times, Do = [Draw()] }],
    };

    /// <summary>"Once while face-up on the field, ...: ... You can only use this effect of "Baronne de Fleur" once per turn."</summary>
    public static CardProject Baronne(OncePerTurn limit = OncePerTurn.FaceUpAndHard) => new()
    {
        Card = Monster(BaronneId, "Baronne de Fleur", CardAttribute.Wind, CardRace.Plant, 4, 3000),
        Effects = [new EffectEvent { Label = "Negate", When = B("when.ignition"), Limit = limit, Do = [Draw()] }],
    };

    /// <summary>Voloferniges: one "once per turn" for its Ignition Effect and the Quick Effect it is with Dragon material.</summary>
    public static CardProject Volo() => new()
    {
        Card = Monster(VoloId, "Voloferniges", CardAttribute.Dark, CardRace.Dragon, 4, 2500),
        Effects =
        [
            new EffectEvent { Label = "Destroy", When = B("when.ignition"), Limit = OncePerTurn.Soft, SharedLimitGroup = 1, If = [B("if.phase").With("phase", "main")], Do = [Draw()] },
            new EffectEvent { Label = "Destroy (Quick)", When = B("when.quick"), Limit = OncePerTurn.Soft, SharedLimitGroup = 1, Do = [Draw()] },
        ],
    };

    /// <summary>"Mimighoul" monsters you control that were not Normal or Special Summoned this turn gain ATK.</summary>
    public static CardProject Mimighoul() => new()
    {
        Card = new CardData { Id = MimighoulId, Name = "Mimighoul Dungeon", Kind = CardKind.Spell, SpellKind = SpellKind.Field },
        Effects =
        [
            new EffectEvent
            {
                Label = "Boost", When = B("when.while_face_up"),
                Do =
                [
                    B("passive.boost_cards").With("amount", 500).With("cards", new CardFilter
                    {
                        Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true,
                        Requirements = [new CardRequirement { Property = "normal_special_turn", Op = RequirementOp.IsNot }],
                    }),
                ],
            },
        ],
    };

    /// <summary>"If a face-up Fusion Monster(s) you control is sent to the GY, except by battle, while this card is in your GY".</summary>
    public static CardProject Aiwass() => new()
    {
        Card = Monster(AiwassId, "Aiwass, Divine Spirit of the Law", CardAttribute.Light, CardRace.Spellcaster, 4, 1500),
        Effects =
        [
            new EffectEvent
            {
                Label = "Revive", Limit = OncePerTurn.Hard,
                When = B(CardsDetailTrigger.BlockId).With("event", "sent_gy").With("whose", "before").With("from", "mzone").With("face", "up").With("by", "not_battle")
                    .With("zone", "graveyard").With("other", "not_this")
                    .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion }),
                Do = [B("do.special_summon").With("cards", new CardFilter { ThisCard = true })],
            },
        ],
    };

    public static IEnumerable<CardProject> All() =>
    [
        Yummy(), Riseheart(), Heruvim(), GilGarth(), Leading(), Heart(), Tactics(), Diablosis(), Preparations(), Nibiru(), Caliga(), Droll(), Reindsurm(),
        GearBox(), Genius(), CounterSpell(), Sebek(), Roller(), Toddler(), Jester(), Lagoon(), Ash(), Karin(), Virakam(), Mechaba(), ToonWorld(), Baronne(),
        Volo(), Mimighoul(), Aiwass(),
    ];
}
