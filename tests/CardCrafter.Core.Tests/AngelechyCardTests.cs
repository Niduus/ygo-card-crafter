using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The Angelechy cards built from blocks, effect by effect, as their card text says them: each compiles cleanly and
/// passes the checker.
/// </summary>
public class AngelechyCardTests
{
    private const int Angelechy = 0x1e2;
    private static readonly CardPack Pack = new() { Archetypes = [new Archetype { Code = Angelechy, Name = "Angelechy" }] };

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance Linked(BlockInstance b, ActionLink link, bool optional = false)
    {
        b.Link = link;
        b.Optional = optional;
        return b;
    }

    private static CardData Synchro(long id, string name) =>
        new() { Id = id, Name = name, Frame = MonsterFrame.Synchro, Level = 7, Atk = 2500, Def = 2000, Race = CardRace.Fairy, Attribute = CardAttribute.Light, Archetypes = [Angelechy] };

    private static CardData Trap(long id, string name) => new() { Id = id, Name = name, Kind = CardKind.Trap, Archetypes = [Angelechy] };

    private static CardData Spell(long id, string name, SpellKind kind) => new() { Id = id, Name = name, Kind = CardKind.Spell, SpellKind = kind, Archetypes = [Angelechy] };

    private static CardFilter Mine(Zone zones, FilterCardType type = FilterCardType.Monster) => new() { Owner = CardOwner.You, Zones = zones, CardType = type, Archetype = Angelechy };

    private static CardRequirement Columns(RequirementOp op, int n) => new() { Property = CardProperties.ColumnsAway, Op = op, Value = n };

    private static EffectEvent Hard(EffectEvent e, int group)
    {
        e.Limit = OncePerTurn.Hard;
        e.SharedLimitGroup = group;
        return e;
    }

    private static EffectEvent Ignition(params BlockInstance[] dos) => new() { When = B("when.ignition", ("zone", "monster_zone")), Do = [.. dos] };

    private static EffectEvent Placed(params BlockInstance[] dos) =>
        new() { When = B(ThisCardEventTrigger.BlockId, ("event", ThisCardEventTrigger.PlacedAsSpellKey)), Do = [.. dos] };

    private static EffectEvent WhileSpell(params BlockInstance[] passives) => new() { When = B("when.while_face_up", ("as", "spell")), Do = [.. passives] };

    private static BlockInstance BanishTarget(CardFilter f) => B("do.banish", ("target", "true")).With("cards", f);

    private static BlockInstance Search(CardFilter f) => B("do.add_to_hand").With("cards", f);

    private static BlockInstance PlaceFromExtra(CardFilter f) => B(PlaceAction.BlockId, ("as", "continuous_spell")).With("cards", f);

    private static CardFilter WithDifferentName(CardFilter f)
    {
        f.Requirements.Add(new CardRequirement { Property = "unlike_names", Op = RequirementOp.Is, Values = [0] });
        return f;
    }

    public static IEnumerable<object[]> Cards()
    {
        // Angelechy Bastion.
        var bastion = new CardProject
        {
            Card = Synchro(28904860, "Angelechy Bastion"),
            Effects =
            [
                Hard(Ignition(BanishTarget(new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, InThisColumn = true, ExcludeThisCard = true })), 1),
                Hard(Placed(PlaceFromExtra(new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, CardCode = 42410161 })), 2),
                WhileSpell(B("passive.protect_cards", ("by", "opponent_effect")).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, Archetype = Angelechy, ExcludeThisCard = true })),
            ],
        };
        // Angelechy Castellan: "1 opponent's monster in this card's adjacent column, or 1 card in your opponent's Spell & Trap
        // Zone that is 2 columns away from this card's".
        var castellanPick = new CardFilter
        {
            Owner = CardOwner.Opponent, Zones = Zone.Field, CardType = FilterCardType.Monster, Requirements = [Columns(RequirementOp.Equal, 1)],
            OrCards = new CardFilter { Requirements = [new CardRequirement { Property = "location", Op = RequirementOp.Is, Values = [0x8] }, Columns(RequirementOp.Equal, 2)] },
        };
        var castellan = new CardProject
        {
            Card = Synchro(101402090, "Angelechy Castellan"),
            Effects =
            [
                Hard(Ignition(BanishTarget(castellanPick)), 1),
                Hard(Placed(B("do.send_to_gy").With("cards", Mine(Zone.Deck, FilterCardType.Any))), 2),
                WhileSpell(B("passive.player_rule", ("rule", "one_per_chain"))),
            ],
        };
        // Angelechy Destrier.
        var destrier = new CardProject
        {
            Card = Synchro(55393975, "Angelechy Destrier"),
            Effects =
            [
                Hard(Ignition(BanishTarget(new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [Columns(RequirementOp.NotEqual, 0)] })), 1),
                Hard(Placed(Search(Mine(Zone.Deck, FilterCardType.Spell))), 2),
                WhileSpell(B(EachTimePassive.BlockId, ("event", "opp_activates"), ("then", "damage"), ("amount", "500"), ("timing", "resolved"))),
            ],
        };
        // Angelechy Shatranga.
        var shatranga = new CardProject
        {
            Card = Synchro(42410161, "Angelechy Shatranga"),
            Effects =
            [
                Hard(Ignition(BanishTarget(new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })), 1),
                Hard(Placed(Search(Mine(Zone.Deck | Zone.Graveyard, FilterCardType.Trap))), 2),
                WhileSpell(B("passive.player_rule", ("rule", "one_monster_effect"), ("whose", "opponent"), ("count", "5"))),
            ],
        };
        // Angelechy Enlisted.
        var others = Mine(Zone.ExtraDeck);
        var enlisted = new CardProject
        {
            Card = Synchro(81797573, "Angelechy Enlisted"),
            Effects =
            [
                Hard(Ignition(BanishTarget(new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [Columns(RequirementOp.Equal, 1)] }),
                    Linked(B("do.this_card", ("how", "control_adjacent")), ActionLink.Then)), 1),
                Hard(new EffectEvent
                {
                    When = B(ThisCardEventTrigger.BlockId, ("event", "control_changed"), ("optional", "false")),
                    Do = [B("do.this_card", ("how", "deck")), Linked(B("do.special_summon", ("who", "owner")).With("cards", others), ActionLink.Then)],
                }, 2),
            ],
        };
        // Angelechy Seneschal.
        var fairy = new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro, Race = CardRace.Fairy };
        var seneschal = new CardProject
        {
            Card = Synchro(101402091, "Angelechy Seneschal"),
            Effects =
            [
                Hard(new EffectEvent
                {
                    When = B("when.summoned", ("how", "special"), ("optional", "false")),
                    Do = [B("do.special_summon", ("who", "each"), ("negate", "true"), ("no_xyz_link", "true"), ("return_extra", "true"), ("self_too", "true")).With("cards", fairy)],
                }, 1),
                Hard(new EffectEvent
                {
                    When = B("when.attack_declared", ("mode", "trigger"), ("how", "targeted"), ("zone", "monster_zone"), ("optional", "false"))
                        .With("attacker", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = Angelechy })
                        .With("target", new CardFilter { ThisCard = true }),
                    Do = [B("do.negate_attack"), Linked(B("do.this_card", ("how", "control")), ActionLink.IfYouDo)],
                }, 2),
            ],
        };
        // Angelechy Opposition: the 2 monsters, Summoned and placed one after the other (each with a different name from the
        // cards in your Spell & Trap Zone), and the Extra Deck lock of the turn it is activated.
        var lockCost = B("cost.summon_restriction", ("what", "special_extra"), ("when", "turn"))
            .With("cards", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro, Requirements = [new CardRequirement { Property = "attribute", Op = RequirementOp.Is, Values = [(long)CardAttribute.Light, (long)CardAttribute.Dark] }] });
        var backToDeck = Mine(Zone.Graveyard | Zone.Banished, FilterCardType.Any);
        backToDeck.ExceptOwnName = true;
        backToDeck.FaceUpOnly = true;
        var opposition = new CardProject
        {
            Card = Spell(101402094, "Angelechy Opposition", SpellKind.Normal),
            Effects =
            [
                Hard(new EffectEvent
                {
                    When = B("when.activate"), Cost = [lockCost],
                    Do =
                    [
                        B("do.special_summon", ("zone", "opp_main")).With("cards", WithDifferentName(Mine(Zone.ExtraDeck))),
                        Linked(PlaceFromExtra(WithDifferentName(Mine(Zone.ExtraDeck))), ActionLink.IfYouDo),
                    ],
                }, 1),
                new EffectEvent
                {
                    When = B("when.ignition", ("zone", "graveyard")), Cost = [B("cost.this_card", ("how", "banish"))],
                    Do = [B("do.shuffle_into_deck", ("mode", "up_to"), ("count", "5")).With("cards", backToDeck)],
                },
            ],
        };
        // Angelechy Verdict.
        var counted = new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, CardType = FilterCardType.Monster, MonsterCards = true, FaceUpOnly = true, Archetype = Angelechy };
        var verdictSend = Mine(Zone.Hand | Zone.Field, FilterCardType.Any);
        verdictSend.FaceUpOnly = true;
        var verdict = new CardProject
        {
            Card = Trap(101402096, "Angelechy Verdict"),
            Effects =
            [
                Hard(new EffectEvent
                {
                    When = B("when.activate"),
                    Do =
                    [
                        B(ChooseEffectsAction.BlockId, ("style", "count")),
                        B(CompareValuesCondition.BlockId, ("value", "count"), ("op", "between"), ("amount", "1"), ("amount2", "4")).With(ValueSources.CardsKey, counted),
                        B("do.negate_effects", ("duration", "always")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Field, FaceUpOnly = true }),
                        Linked(B(CompareValuesCondition.BlockId, ("value", "count"), ("op", "ge"), ("amount", "5")).With(ValueSources.CardsKey, counted), ActionLink.Option),
                        B("do.banish", ("mode", "all")).With("cards", CardFilter.OpponentField()),
                    ],
                }, 1),
                Hard(new EffectEvent
                {
                    When = B("when.quick", ("zone", "graveyard")), Cost = [B("cost.this_card", ("how", "banish"))],
                    Do = [B("do.send_to_gy").With("cards", verdictSend), Linked(PlaceFromExtra(WithDifferentName(Mine(Zone.ExtraDeck))), ActionLink.Then, optional: true)],
                }, 2),
            ],
        };
        // Angelechy Disturbance.
        var beside = new CardFilter
        {
            Owner = CardOwner.Opponent, Zones = Zone.Field, FaceUpOnly = true,
            Requirements = [new CardRequirement { Property = "ref_adjacent", Op = RequirementOp.Is, Values = [(long)CardRef.Affected] }],
        };
        var disturbanceSearch = Mine(Zone.Deck, FilterCardType.SpellTrap);
        disturbanceSearch.ExceptOwnName = true;
        var disturbance = new CardProject
        {
            Card = Trap(54171327, "Angelechy Disturbance"),
            Effects =
            [
                Hard(new EffectEvent
                {
                    When = B("when.activate"),
                    Do = [B("do.take_control", ("to", "opponent"), ("target", "false")).With("cards", Mine(Zone.MonsterZone)), Linked(B("do.negate_effects", ("mode", "all"), ("duration", "always")).With("cards", beside), ActionLink.Then)],
                }, 1),
                Hard(new EffectEvent { When = B("when.quick", ("zone", "graveyard")), Cost = [B("cost.this_card", ("how", "banish"))], Do = [Search(disturbanceSearch)] }, 2),
            ],
        };
        // Angelechy Opening to e4.
        var level27 = Mine(Zone.ExtraDeck);
        level27.Requirements = [new CardRequirement { Property = "level", Op = RequirementOp.Equal, Value = 2, Group = 1 }, new CardRequirement { Property = "level", Op = RequirementOp.Equal, Value = 7, Group = 1 }];
        var openingLock = B("do.summon_restriction", ("duration", "your_next"), ("who", "you"), ("what", "special_extra"))
            .With("cards", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro });
        var opening = new CardProject
        {
            Card = Trap(80565021, "Angelechy Opening to e4"),
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.while_face_up"),
                    If = [B("if.turn", ("whose", "opponent")), B("if.phase", ("phase", "standby")), B("if.turn", ("whose", "first"))],
                    Do = [B(ActivateFromHandPassive.BlockId)],
                },
                new EffectEvent
                {
                    When = B("when.activate"),
                    Do =
                    [
                        B(PlaceAction.BlockId, ("where", "fzone")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck | Zone.Graveyard, CardType = FilterCardType.Spell, SpellTrapKind = SpellTrapKind.Field, Archetype = Angelechy }),
                        Linked(B("do.special_summon", ("zone", "extra_zone")).With("cards", level27), ActionLink.IfYouDo),
                        Linked(PlaceFromExtra(Mine(Zone.ExtraDeck)), ActionLink.IfYouDo),
                        Linked(openingLock, ActionLink.AlsoAfterThat),
                    ],
                },
            ],
        };
        // Angelechy Endgame Problem: each effect with how many "Angelechy" Monster Cards it needs ("● 2+: When your opponent
        // would Special Summon a monster(s), you choose the zone(s) to place it" has no engine effect: see AngelechyTests).
        BlockInstance AtLeast(int n) => B("if.cards_exist", ("cmp", "at_least"), ("count", n.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Monster, MonsterCards = true, FaceUpOnly = true, Archetype = Angelechy });
        var endgame = new CardProject
        {
            Card = Spell(101402095, "Angelechy Endgame Problem", SpellKind.Field),
            Effects =
            [
                new EffectEvent { When = B("when.phase", ("whose", "opponent"), ("phase", "standby"), ("zone", "field_zone")), If = [AtLeast(1)], Limit = OncePerTurn.Soft, Do = [B("do.draw")] },
                new EffectEvent
                {
                    When = B("when.while_face_up", ("zone", "field_zone")), If = [AtLeast(3)], Limit = OncePerTurn.Soft,
                    Do = [B(ActivateSetTurnPassive.BlockId, ("which", "cards")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Trap, Archetype = Angelechy })],
                },
                new EffectEvent
                {
                    When = B("when.event", ("event", "opp_special"), ("zone", "field_zone"), ("wording", "when")), If = [AtLeast(4)], Limit = OncePerTurn.Soft,
                    Do = [B("do.banish", ("mode", "all")).With("cards", new CardFilter { That = CardRef.Event, CardType = FilterCardType.Monster })],
                },
                new EffectEvent { When = B("when.ignition", ("zone", "field_zone")), If = [AtLeast(5)], Limit = OncePerTurn.Soft, Do = [B("do.special_summon").With("cards", Mine(Zone.ExtraDeck))] },
            ],
        };
        // Junora the Power Patron of Tuning (its Synchro Summon with a monster in the center Main Monster Zone as a Tuner is the Card tab's).
        var junora = new CardProject
        {
            Card = new CardData { Id = 5914858, Name = "Junora the Power Patron of Tuning", Frame = MonsterFrame.Synchro, Level = 10, Atk = 3000, Def = 3000, Race = CardRace.Fairy, Attribute = CardAttribute.Light },
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.summoned", ("how", "synchro")),
                    Do = [B("do.negate_effects", ("mode", "all"), ("duration", "always")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Field, FaceUpOnly = true })],
                },
                Hard(new EffectEvent
                {
                    When = B("when.quick", ("zone", "monster_zone")),
                    Do =
                    [
                        B("do.apply_effects", ("mode", "all"), ("negate", "false"), ("no_attack", "false"), ("indestructible", "false"), ("unaffected", "false"), ("no_tribute", "false"))
                            .With("cards", new CardFilter
                            {
                                Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster,
                                Requirements = [new CardRequirement { Property = CardProperties.SameColumn, Op = RequirementOp.Is, Cards = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Archetype = 0x1aa } }],
                            }),
                    ],
                }, 1),
            ],
        };
        foreach (var p in new[] { bastion, castellan, destrier, shatranga, enlisted, seneschal, opposition, verdict, disturbance, opening, endgame, junora })
            yield return [p.Card.Name, p];
    }

    [Theory]
    [MemberData(nameof(Cards))]
    public void BuildsFromBlocks(string name, CardProject card)
    {
        var result = ScriptCompiler.Compile(card, Pack);
        Assert.False(result.HasErrors, $"{name}: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Empty(Validation.ScriptChecker.Check(result.Lua));
        Assert.NotEqual("", EffectTextGenerator.Generate(card, Pack));
        if (Environment.GetEnvironmentVariable("ANGELECHY_DUMP") is { Length: > 0 } dump)
            File.AppendAllText(dump, $"===== {name}\n{EffectTextGenerator.Generate(card, Pack)}\n{string.Join("\n", result.Diagnostics.Select(d => d.Severity + ": " + d.Message))}\n");
    }
}
