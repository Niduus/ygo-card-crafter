using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Validation;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Compiles every block with every choice and flag value, and checks each script parses as Lua 5.4
/// and only uses functions and constants that exist in the engine.
/// </summary>
public class AllBlocksTests
{
    public static IEnumerable<object[]> Variants()
    {
        foreach (var def in BlockRegistry.All)
            foreach (var (label, inst) in VariantsOf(def))
                yield return [def.Id, label, inst];
    }

    private static IEnumerable<(string, BlockInstance)> VariantsOf(BlockDefinition def)
    {
        yield return ("defaults", def.CreateInstance());
        foreach (var p in def.Params)
        {
            switch (p.Type)
            {
                case ParamType.Choice:
                    foreach (var c in Samples.OtherChoices(p))
                    {
                        // Special Summoning or Setting "all" is refused (each card needs a free zone); BlockModeTests checks that.
                        if (p.Key == "mode" && c.Value == "all" && def.Id is "do.special_summon" or "do.set" or "do.place") continue;
                        var inst = def.CreateInstance().With(p.Key, c.Value);
                        // "All" cards aren't targeted.
                        if (c.Value == "all" && p.Key is "mode" or "count_mode" && def.Param("target") is not null) inst.Values["target"] = "false";
                        if (def.Id == "cost.move_cards" && c.Value == "grave") inst.Filters["cards"].Zones = Zone.Hand;
                        yield return ($"{p.Key}={c.Value}", inst);
                    }
                    break;
                case ParamType.Bool:
                    var flipped = def.CreateInstance().With(p.Key, p.Default == "true" ? "false" : "true");
                    // Targeting needs a public zone to aim at.
                    if (p.Key == "target" && flipped.Filters.TryGetValue("cards", out var f) && (f.Zones & (Zone.Deck | Zone.Hand | Zone.ExtraDeck)) != Zone.None)
                        f.Zones = Zone.Graveyard;
                    // This card itself is never targeted: aim at the opponent's monsters.
                    if (p.Key == "target" && flipped.Filters.TryGetValue("cards", out var own) && own.ThisCard && flipped.Values["target"] == "true")
                        flipped.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
                    yield return ($"{p.Key}=flipped", flipped);
                    break;
                case ParamType.Integer:
                    if (p.Max > 1) yield return ($"{p.Key}=2", def.CreateInstance().With(p.Key, Math.Max(2, p.Min)));
                    break;
                // "This card itself" in the picker; this card is never targeted.
                case ParamType.Cards when p.AllowThisCard:
                    var self = def.CreateInstance();
                    self.Filters[p.Key].ThisCard = true;
                    if (def.Param("target") is not null) self.Values["target"] = "false";
                    yield return ($"{p.Key}=this card", self);
                    break;
            }
            // "That card": the target, the cards the step before affected, the cost's cards, a battling monster.
            if (p is { Type: ParamType.Cards, AllowThatCard: true })
                foreach (var that in Enum.GetValues<CardRef>().Where(r => r != CardRef.None))
                {
                    // A Continuous Spell ties the monster it Summoned or targeted, never a battling one.
                    if (def.Id == BindAction.BlockId && that is CardRef.BattleTarget or CardRef.Battling) continue;
                    var inst = def.CreateInstance();
                    inst.Filters[p.Key] = new CardFilter { That = that };
                    if (def.Param("target") is not null) inst.Values["target"] = "false";
                    yield return ($"{p.Key}=that {that}", inst);
                }
        }
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void BlockVariantCompilesToValidLua(string blockId, string variant, BlockInstance inst)
    {
        var def = BlockRegistry.Find(blockId)!;
        var project = ProjectFor(def, inst);
        var result = ScriptCompiler.Compile(project, Pack(project));

        var errors = result.Diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        Assert.True(errors.Count == 0, $"{blockId} [{variant}]: {string.Join("; ", errors)}\n{result.Lua}");
        var checks = ScriptChecker.Check(result.Lua);
        Assert.True(checks.Count == 0, $"{blockId} [{variant}]: {string.Join("; ", checks)}\n{result.Lua}");
    }

    private const long TokenId = 100000099;

    /// <summary>The pack the card is compiled in, with a Token card for "Special Summon Tokens".</summary>
    private static CardPack Pack(CardProject project) => new()
    {
        Cards =
        [
            project,
            new CardProject { Card = new CardData { Id = TokenId, Name = "Sheep Token", Frame = MonsterFrame.Token, Level = 1, Race = CardRace.Beast, Attribute = CardAttribute.Earth } },
        ],
    };

    /// <summary>Wraps a single block in the smallest effect that makes sense for it.</summary>
    /// <summary>Blocks about Link Arrows need a Link monster to sit on.</summary>
    private static bool NeedsLink(BlockDefinition def, BlockInstance inst) =>
        def.Id is "if.points_to" || inst.Values.GetValueOrDefault("zone") == "pointed";

    private static CardProject ProjectFor(BlockDefinition def, BlockInstance inst)
    {
        var project = ProjectFor0(def, inst);
        if (NeedsLink(def, inst))
        {
            project.Card.Frame = MonsterFrame.Link;
            project.Card.LinkMarkers = LinkMarker.Left | LinkMarker.Right;
            project.Procedure.Kind = SummonProcedureKind.Link;
        }
        if (def.Id == "do.negate") project.Effects[0].When = new BlockInstance("when.quick").With("timing", "chain_any");
        // "Activate this card": a Trap, watching anyone's battle.
        if (def.Kind == BlockKind.Trigger && inst.Values.GetValueOrDefault("mode") == "activate")
        {
            project.Card.Kind = CardKind.Trap;
            if (def.Id == BattleWindowTrigger.BlockId) inst.Values["who"] = "any";
        }
        switch (def.Id)
        {
            case "do.negate_summon":
                project.Effects[0].When = new BlockInstance("when.quick").With("timing", "would_summon");
                break;
            case "do.attach":
                project.Card.Frame = MonsterFrame.Xyz;
                project.Procedure.Kind = SummonProcedureKind.Xyz;
                break;
            case "passive.equipped" or "passive.equip_value":
                project.Card.Kind = CardKind.Spell;
                project.Card.SpellKind = SpellKind.Equip;
                break;
            case "do.tokens":
                inst.Values["token"] = TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // "The combined original ATK/DEF of the Tributed monsters": after a block that Tributes.
                if (inst.Values.GetValueOrDefault("stats") == "tributed") project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.tribute")!.CreateInstance());
                break;
            // "Instead of conducting your normal draw" is for the Draw Phase.
            case "when.phase" when inst.Values.GetValueOrDefault("instead") == "true":
                inst.Values["phase"] = "draw";
                break;
            // "All of them on this card" moves this card's counters onto other cards.
            case PlaceCounterAction.BlockId when inst.Values.GetValueOrDefault("by") == "here":
                inst.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
                break;
            case ArchetypeActivationsCondition.BlockId:
                inst.Filters["cards"].Archetype = ArchetypeActivationsCondition.Mulcharmy;
                break;
            // "The card(s) the step before affected", "this effect's target(s)": after a block that destroys or targets them.
            case ControllerDamageAction.BlockId when inst.Values.GetValueOrDefault("which") is "affected" or "target":
                project.Effects[0].Do.Insert(0, inst.Values["which"] == "affected" ? BlockRegistry.Find("do.destroy")!.CreateInstance() : BlockRegistry.Find(TargetCardsAction.BlockId)!.CreateInstance());
                break;
            // Responses to an activation.
            case "do.negate_unless" or "do.that_card":
                project.Effects[0].When = new BlockInstance("when.quick").With("timing", "chain_any");
                break;
            case "cost.reveal_self" or "cost.reveal" when def.Id == "cost.reveal_self" || inst.Filters["cards"].ThisCard:
                project.Effects[0].When = new BlockInstance("when.ignition").With("zone", "hand");
                break;
            // A Set Trap's column; a Trap activated from the hand.
            case "do.negate_column":
                project.Card.Kind = CardKind.Trap;
                project.Effects[0].When = new BlockInstance("when.activate");
                break;
            case "passive.activate_from_hand" or ActivateSetTurnPassive.BlockId:
                project.Card.Kind = CardKind.Trap;
                break;
            // "Ritual Summon this card": a Ritual Monster's effect in the hand.
            case RitualSummonAction.BlockId when inst.Filters["cards"].ThisCard:
                project.Card.Frame = MonsterFrame.Ritual;
                project.Effects[0].When = new BlockInstance("when.ignition").With("zone", "hand");
                break;
            case "passive.replace_destroy" when inst.Values.GetValueOrDefault("how") == "detach":
                project.Card.Frame = MonsterFrame.Xyz;
                project.Procedure.Kind = SummonProcedureKind.Xyz;
                break;
            // A Gemini monster's effects; a Trap that becomes a monster when activated.
            case GeminiCondition.BlockId:
                project.Card.Abilities = MonsterAbility.Gemini;
                break;
            case TrapMonsterAction.BlockId:
                project.Card.Kind = CardKind.Trap;
                project.Effects[0].When = new BlockInstance("when.activate");
                break;
            // Blaze Cannon: the effects it gives, marked "given to a monster".
            case GrantEffectsAction.BlockId:
                project.Effects.Add(new EffectEvent
                {
                    Granted = true, When = new BlockInstance("when.while_face_up"),
                    Do = [BlockRegistry.Find("passive.immune")!.CreateInstance().With("cards", new CardFilter { ThisCard = true, Zones = Zone.MonsterZone })],
                });
                break;
            // "Draw cards equal to the number of monsters destroyed by this effect and sent to your opponent's GY": after the block that destroys them.
            case "do.draw" when inst.Values.GetValueOrDefault("per") == "per_sent_opp":
                project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.destroy")!.CreateInstance());
                break;
            // "..., but destroy it during the End Phase": after the block that Summons it.
            case EndPhaseAction.BlockId:
                project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.special_summon")!.CreateInstance());
                break;
            // A coin toss or die roll's result goes among the DO blocks, after the toss or roll.
            case CoinResultCondition.BlockId:
            {
                var result = inst.Values.GetValueOrDefault("result");
                var toss = BlockRegistry.Find(TossCoinAction.BlockId)!.CreateInstance()
                    .With("call", result is "right" or "wrong").With("count", result is "heads_n" or "heads_min" or "tails_n" ? 3 : 1);
                project.Effects[0].If.Clear();
                project.Effects[0].Do.InsertRange(0, [toss, inst]);
                break;
            }
            case DieResultCondition.BlockId:
                if (!inst.Values.Any(v => v.Value == "true")) inst.Values["r6"] = "true";
                project.Effects[0].If.Clear();
                project.Effects[0].Do.InsertRange(0, [BlockRegistry.Find(RollDieAction.BlockId)!.CreateInstance(), inst]);
                break;
            case DeclaredLockAction.BlockId:
                project.Effects[0].Do.Insert(0, BlockRegistry.Find(DeclareAction.BlockId)!.CreateInstance());
                break;
            // "That Attribute": what a declaration before it declared.
            case BecomeAction.BlockId when inst.Values.GetValueOrDefault("what") is "declared_attribute" or "declared_race":
                project.Effects[0].Do.Insert(0, BlockRegistry.Find(DeclareAction.BlockId)!.CreateInstance().With("what", inst.Values["what"] == "declared_race" ? "race" : "attribute"));
                break;
            // Remembering or a delayed step on "the cards the step before affected" or "the target": after a block that targets them.
            case RememberAction.BlockId or LaterAction.BlockId:
                project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.destroy")!.CreateInstance().With("target", true));
                // Cards are forgotten when they move, not while this card is face-up: that time is for numbers.
                if (inst.Values.GetValueOrDefault("duration") == "face_up") inst.Values["what"] = "value";
                break;
            // Only Spells and Traps are Set; "Choose 1 of these effects" comes before two "●" options.
            case RevealPickAction.BlockId when inst.Values.GetValueOrDefault("then") == "set":
                inst.Filters["cards"].CardType = FilterCardType.SpellTrap;
                break;
            // "Based on where it was sent from": each option starts with where this card was.
            case ChooseEffectsAction.BlockId when inst.Values.GetValueOrDefault("style") == "sent_from":
                project.Effects[0].Do.AddRange(
                [
                    new BlockInstance(SentFromCondition.BlockId).With("from", "field"), new BlockInstance("do.draw"),
                    new BlockInstance(SentFromCondition.BlockId).With("from", "hand").Linked(ActionLink.Option), new BlockInstance("do.gain_lp"),
                ]);
                break;
            // Materials of this effect's target or of this card: an Xyz Monster.
            case FromMaterialsAction.BlockId:
                project.Card.Frame = MonsterFrame.Xyz;
                project.Procedure.Kind = SummonProcedureKind.Xyz;
                if (inst.Values.GetValueOrDefault("from", "target") == "target")
                    project.Effects[0].Do.Insert(0, BlockRegistry.Find(TargetCardsAction.BlockId)!.CreateInstance());
                break;
            // "That many": a cost that discards any number of cards.
            case SendZonesAction.BlockId when inst.Values.GetValueOrDefault("many") == "cost":
                project.Effects[0].Cost.Add(new BlockInstance("cost.discard").With("mode", "any"));
                break;
            // The Spell sent for the cost lends its effect; a Continuous Spell ties the monster it Summoned.
            case CopyEffectAction.BlockId:
                project.Effects[0].Cost.Add(new BlockInstance("cost.move_cards").With("how", "grave").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Spell }));
                break;
            case BindAction.BlockId:
                project.Card.Kind = CardKind.Spell;
                project.Card.SpellKind = SpellKind.Continuous;
                project.Effects[0].When = new BlockInstance("when.activate");
                if (inst.Filters["cards"].That == CardRef.Affected) project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.special_summon")!.CreateInstance());
                break;
            case ChooseEffectsAction.BlockId:
                project.Effects[0].Do.AddRange([new BlockInstance("do.draw").Linked(ActionLink.Option), new BlockInstance("do.gain_lp").Linked(ActionLink.Option)]);
                break;
            // "Its owner draws": the owner of the card the step before moved.
            case "do.draw" when inst.Values.GetValueOrDefault("who") == "owner":
                project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.destroy")!.CreateInstance());
                break;
            // Equipped to the monster this card is equipped to: something else than this card is equipped.
            case EquipAction.BlockId when inst.Filters["to"].That == CardRef.Equipped:
                inst.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster };
                break;
            // Giving one of this card's effects: effects listed before it to give.
            case GrantEffectPassive.BlockId:
                project.Effects.InsertRange(0, [
                    new EffectEvent { When = new BlockInstance("when.ignition"), Do = [new BlockInstance("do.draw")] },
                    new EffectEvent { When = new BlockInstance("when.ignition"), Do = [new BlockInstance("do.gain_lp")] }]);
                break;
        }
        // "That card", "the targeted monster's ATK", "and the target": a block before that targets, affects cards, a cost, a battle.
        var refs = inst.Filters.Values.Select(f => f.That).ToHashSet();
        var value = inst.Values.GetValueOrDefault("value", "");
        if (def.Kind == BlockKind.Action && (refs.Contains(CardRef.Target) || value.StartsWith("target_", StringComparison.Ordinal)
                                             || inst.Values.GetValueOrDefault("plus") == "target" || inst.Values.GetValueOrDefault("with") == "target"))
        {
            // One block of an effect targets.
            if (inst.Values.ContainsKey("target")) inst.Values["target"] = "false";
            if (!project.Effects[0].Do.Any(b => b.Values.GetValueOrDefault("target") == "true"))
                project.Effects[0].Do.Insert(0, BlockRegistry.Find(TargetCardsAction.BlockId)!.CreateInstance());
        }
        if (def.Kind == BlockKind.Action && (refs.Contains(CardRef.Affected) || inst.Values.GetValueOrDefault("is") is "it_is" or "any_was"))
            project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.destroy")!.CreateInstance());
        if (def.Kind == BlockKind.Action && value == "damage_dealt") project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.damage")!.CreateInstance());
        if (refs.Contains(CardRef.Cost) || value.StartsWith("cost_", StringComparison.Ordinal))
            project.Effects[0].Cost.Add(new BlockInstance("cost.move_cards").With("how", "banish"));
        if (refs.Contains(CardRef.BattleTarget) || refs.Contains(CardRef.Battling)) project.Effects[0].When = new BlockInstance("when.destroys_by_battle");
        // "The LP paid" needs the cost that pays LP in multiples.
        if (inst.Values.GetValueOrDefault("value") == "lp_paid") project.Effects[0].Cost.Add(new BlockInstance("cost.pay_lp").With("how", "multiple"));
        // "The number of cards used for the cost" and "that many" need a cost that counts: detaching any number.
        if (inst.Values.GetValueOrDefault("value") == "cost_count" || inst.Values.GetValueOrDefault("amount") == "cost" && def.Kind == BlockKind.Action)
        {
            project.Card.Frame = MonsterFrame.Xyz;
            project.Procedure.Kind = SummonProcedureKind.Xyz;
            project.Effects[0].Cost.Add(new BlockInstance("cost.detach").With("amount", "any"));
        }
        // An Extra Deck monster's own procedure "from your Extra Deck".
        if (def.Id == SummonProcedureTrigger.BlockId && inst.Values.GetValueOrDefault("zone") == "extra")
        {
            project.Card.Frame = MonsterFrame.Fusion;
            project.Procedure.Kind = SummonProcedureKind.Fusion;
        }
        // "The result" of a die roll and "the number of heads" need the roll or toss before them.
        if (inst.Values.GetValueOrDefault("value") is "roll" or "heads" && def.Kind == BlockKind.Action)
            project.Effects[0].Do.Insert(0, BlockRegistry.Find(inst.Values["value"] == "roll" ? RollDieAction.BlockId : TossCoinAction.BlockId)!.CreateInstance());
        // "That monster's original ATK" reads the cards a step before affected.
        if (ValueSources.Affected(inst.Values.GetValueOrDefault("value", "")) && def.Kind == BlockKind.Action)
            project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.destroy")!.CreateInstance());
        // "For each card used for the cost" needs a cost that counts; "for each of those cards" needs an action before.
        if (inst.Values.GetValueOrDefault("per") == "per_cost")
            project.Effects[0].Cost.Add(new BlockInstance("cost.move_cards").With("how", "banish"));
        // So does "equal to that monster's ATK".
        if (inst.Values.GetValueOrDefault("per") == "per_previous" || inst.Values.GetValueOrDefault("equal", "amount") != "amount")
            project.Effects[0].Do.Insert(0, BlockRegistry.Find("do.destroy")!.CreateInstance());
        // A card Special Summons itself from where it is, never from the field.
        if (def.Id == "do.special_summon_self") project.Effects[0].When = new BlockInstance("when.ignition").With("zone", "hand");
        // "From where its cost put it": a cost that banishes this card.
        if (def.Id == "do.special_summon_self" && inst.Values.GetValueOrDefault("from") == "cost")
            project.Effects[0].Cost.Add(new BlockInstance("cost.this_card").With("how", "banish"));
        // "If this card was activated this turn": a Continuous Trap, whose activation leaves the flag.
        if (def.Id == ActivatedThisTurnCondition.BlockId)
        {
            project.Card.Kind = CardKind.Trap;
            project.Card.TrapKind = TrapKind.Continuous;
        }
        // "The same card type as the activated card": a cost of a response to an activation.
        if (def.Id == "cost.move_cards" && inst.Values.GetValueOrDefault("same_type") == "true")
            project.Effects[0].When = new BlockInstance("when.quick").With("timing", "chain_any");
        // "If the activation of this card ... was negated": a Trap whose activation watches for it.
        if (def.Id == NegatedActivationTrigger.BlockId)
        {
            project.Card.Kind = CardKind.Trap;
            project.Effects.Insert(0, new EffectEvent { When = new BlockInstance("when.activate"), Do = [BlockRegistry.Find("do.draw")!.CreateInstance()] });
        }
        return project;
    }

    private static CardProject ProjectFor0(BlockDefinition def, BlockInstance inst)
    {
        var effect = new EffectEvent { Label = def.Name };
        CardData card;
        switch (def.Kind)
        {
            case BlockKind.Trigger:
                card = Samples.CardFor(def);
                effect.When = inst;
                if (def.Id != SummonProcedureTrigger.BlockId)
                    effect.Do.Add(def.Id == "when.while_face_up" ? new BlockInstance("passive.untargetable") : new BlockInstance("do.draw"));
                break;
            case BlockKind.Passive:
                card = new CardData { Id = 100000013, Name = "Test" };
                effect.When = new BlockInstance("when.while_face_up");
                effect.Do.Add(inst);
                break;
            default:
                card = new CardData { Id = 100000014, Name = "Test" };
                effect.When = new BlockInstance("when.ignition");
                if (def.Kind == BlockKind.Condition) effect.If.Add(inst);
                if (def.Kind == BlockKind.Cost) effect.Cost.Add(inst);
                if (def.Kind == BlockKind.Action) effect.Do.Add(inst);
                else effect.Do.Add(new BlockInstance("do.draw"));
                break;
        }
        return new CardProject { Card = card, Effects = [effect] };
    }

    [Fact]
    public void EveryBlockHasUniqueIdAndValidSentence()
    {
        Assert.Equal(BlockRegistry.All.Count, BlockRegistry.All.Select(b => b.Id).Distinct().Count());
        foreach (var def in BlockRegistry.All)
        {
            var keys = SentenceParser.Parse(def.Sentence).Where(s => s.Key is not null).Select(s => s.Key!).ToList();
            foreach (var k in keys) Assert.True(def.Param(k) is not null, $"{def.Id}: sentence blank {{{k}}} has no parameter");
            // "exactly / up to / all" sits before the count and "for each" at the end, without a blank of their own.
            // So does "and this card / the target", said with the count ("Special Summon this card and 1 ...").
            foreach (var p in def.Params) Assert.True(keys.Contains(p.Key) || p.Type == ParamType.Bool || p.Key is "mode" or "count_mode" or "per" or "plus", $"{def.Id}: parameter {p.Key} is not in the sentence");
        }
    }
}
