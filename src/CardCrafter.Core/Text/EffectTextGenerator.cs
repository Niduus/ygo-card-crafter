using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>
/// Writes suggested card text from the event sheet, following official wording patterns:
/// "[Once per turn, ][trigger/conditions]: You can [cost], then target [x]; [action], and if you do, [action]."
/// It is a starting point the user can always edit by hand.
/// </summary>
public static partial class EffectTextGenerator
{
    /// <summary>
    /// The card's text. For a Pendulum Monster it is the Monster Effect (or a Normal Monster's flavor text, which is
    /// written by hand): the effects used in the Pendulum Zone go in <see cref="GeneratePendulum"/>.
    /// </summary>
    public static string Generate(CardProject project, CardPack? pack = null) =>
        Generate(project, pack, project.Card.IsPendulum ? project.Effects.Where(e => !IsPendulumEffect(e)).ToList() : project.Effects);

    /// <summary>A Pendulum Monster's Pendulum Effect text: its effects used in the Pendulum Zone. Empty for other cards.</summary>
    public static string GeneratePendulum(CardProject project, CardPack? pack = null)
    {
        if (!project.Card.IsPendulum) return "";
        var effects = project.Effects.Where(IsPendulumEffect).ToList();
        var paragraph = effects.Select(e => Describe(e, project.Card, pack, limitSentence: CombinedLimit(project, effects) is null || !UsesCombinedLimit(e))).Where(s => s.Length > 0).ToList();
        if (CombinedLimit(project, effects) is { } combined) paragraph.Add(combined);
        return Join(paragraph);
    }

    /// <summary>
    /// Both boxes as the card database writes a Pendulum Monster's text: "[ Pendulum Effect ]" and "[ Monster Effect ]"
    /// (or "[ Flavor Text ]"), each followed by its text. Other cards get <see cref="Generate"/>.
    /// </summary>
    public static string GenerateWithSections(CardProject project, CardPack? pack = null)
    {
        if (!project.Card.IsPendulum) return Generate(project, pack);
        var monster = project.Card.Frame == MonsterFrame.Normal ? "[ Flavor Text ]" : "[ Monster Effect ]";
        return $"[ Pendulum Effect ]\n{GeneratePendulum(project, pack)}\n{monster}\n{Generate(project, pack)}";
    }

    /// <summary>An effect used while this card is in the Pendulum Zone: it goes in the Pendulum Effect box.</summary>
    public static bool IsPendulumEffect(EffectEvent effect) => effect.When?.Values.GetValueOrDefault("zone") == "pendulum_zone";

    /// <summary>Sentences joined into one paragraph; after a list of "●" options, the next effect starts on a new line.</summary>
    private static string Join(List<string> paragraph)
    {
        var text = "";
        for (var i = 0; i < paragraph.Count; i++)
            text += (i == 0 ? "" : paragraph[i - 1].Contains(OptionLine, StringComparison.Ordinal) ? "\n" : " ") + paragraph[i];
        return text;
    }

    private static string Generate(CardProject project, CardPack? pack, List<EffectEvent> effects)
    {
        // Printed cards keep the materials on their own line; everything else is one paragraph.
        var paragraph = new List<string>();
        if (MaterialText.Alternate(project, pack) is { Length: > 0 } alternate) paragraph.Add(alternate);
        if (MaterialText.SynchroLinkTuner(project) is { Length: > 0 } tuner) paragraph.Add(tuner);
        if (EquipText(project, pack) is { Length: > 0 } equip) paragraph.Add(equip);
        if (ContactFusion.Text(project) is { Length: > 0 } contact) paragraph.Add(contact);
        else if (RuleText(project) is { Length: > 0 } rule) paragraph.Add(rule);
        var combined = CombinedLimit(project, effects);
        // The Summoning conditions come first, as printed cards have them: "Cannot be Normal Summoned/Set. Must first be
        // Special Summoned ...", then "You can only Special Summon ... once per turn." and "You can only control 1 ...".
        var reminder = ArchetypeReminder.Line(project, pack);
        var late = LateActivationLimit(effects);
        string Said(EffectEvent e)
        {
            var text = Describe(e, project.Card, pack, limitSentence: (combined is null || !UsesCombinedLimit(e)) && !ReferenceEquals(e, late));
            // A "This card is always treated as" block's own reminder is the card's line instead (none when the name says it).
            return project.Card.ArchetypeReminder ? ArchetypeReminder.WithoutReminder(text) : text;
        }
        bool Procedure(EffectEvent e) => e.When?.BlockId == SummonProcedureTrigger.BlockId;
        paragraph.AddRange(effects.Where(Procedure).Select(Said).Where(s => s.Length > 0));
        if (project.Card.IsMonster && project.Card.CannotBeSpecialSummoned && !project.Card.OnlyByOwnProcedure && !ContactFusion.Applies(project)) paragraph.Add("Cannot be Special Summoned.");
        if (project.Card.IsMonster && project.Card.SpecialSummonOncePerTurn) paragraph.Add($"You can only Special Summon \"{project.Card.Name}\" once per turn.");
        if (project.Card.OnlyOneOnField) paragraph.Add($"You can only control 1 \"{project.Card.Name}\".");
        if (SpiritText(project) is { Length: > 0 } spirit) paragraph.Add(spirit);
        if (GeminiText(project, pack, combined) is { Length: > 0 } gemini) paragraph.Add(gemini);
        paragraph.AddRange(effects.Where(e => !GeminiCondition.Has(e) && !Procedure(e)).Select(Said).Where(s => s.Length > 0));
        if (late is not null) paragraph.Add(ActivationLimit(late, project.Card.Name));
        if (combined is not null) paragraph.Add(combined);
        // The materials, then "(This card is always treated as a "X" card.)", each on its own line.
        return string.Join("\n", new[] { MaterialText.Describe(project, pack), reminder, Join(paragraph) }.Where(s => s.Length > 0));
    }

    /// <summary>
    /// The summoning rule line when no Summon procedure effect already says it: "Cannot be Normal Summoned/Set."
    /// or, for Extra Deck and Ritual monsters, "Must be Fusion Summoned."
    /// </summary>
    private static bool StartsWithSubject(string body) =>
        System.Text.RegularExpressions.Regex.IsMatch(body, @"^(this card |this card's |the (ATK|DEF|ATK/DEF|Level|Levels|battle position)s? of |\d+ |up to \d+ |all |each |both players |you take |once, )");

    /// <summary>What starts each option of "activate 1 of these effects": a new, indented line with a bullet.</summary>
    public const string OptionLine = "\n ● ";

    private static string RuleText(CardProject project)
    {
        var card = project.Card;
        if (!card.IsMonster || card.SummonRule == SummonRule.Normal) return "";
        // "Must be either Fusion Summoned, or Special Summoned by ...": the procedure from the Extra Deck says it.
        if (!card.IsMainDeckNonRitual)
            return card.SummonRule == SummonRule.ProcedureOnly && !project.Effects.Any(IsExtraDeckProcedure) ? $"Must be {card.Frame} Summoned." : "";
        if (card.SummonRule == SummonRule.ByCardEffect) return "Cannot be Normal Summoned/Set. Must be Special Summoned by a card effect.";
        bool hasProcedure = project.Effects.Any(e => e.When?.BlockId == SummonProcedureTrigger.BlockId);
        return hasProcedure ? "" : "Cannot be Normal Summoned/Set.";
    }

    /// <summary>"Equip only to a Warrior monster." for Equip Spells that can't be equipped to every monster.</summary>
    private static string EquipText(CardProject project, CardPack? pack)
    {
        var card = project.Card;
        if (card.Kind != CardKind.Spell || card.SpellKind != SpellKind.Equip) return "";
        var f = project.EquipFilter;
        bool restricted = f.MonsterKind != MonsterKind.Any || f.Race != CardRace.None || f.Attribute != CardAttribute.None || f.Archetype != 0
                          || f.CardCode != 0 || f.LevelMin is not null || f.LevelMax is not null || f.AtkMin is not null || f.AtkMax is not null;
        var where = f.Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => "" };
        if (!restricted && where.Length == 0) return "";
        var copy = f.Clone();
        copy.CardType = FilterCardType.Monster;
        var noun = FilterText.Describe(copy, pack, withLocation: false);
        var article = noun.Length > 0 && "AEIOUaeiou".Contains(noun[0]) ? "an" : "a";
        return $"Equip only to {article} {noun}{where}.";
    }

    /// <summary>Hard once-per-turn effects that are used, not activated as a Spell/Trap Card or a Summon procedure.</summary>
    private static bool UsesCombinedLimit(EffectEvent e) =>
        e.Limit == OncePerTurn.Hard && ScriptCompiler.Times(e) == 1 && e.When is { } w && BlockRegistry.Find(w.BlockId) is { } t
        && t is not (ActivateTrigger or SummonProcedureTrigger or WhileFaceUpTrigger) && !DetailedTriggers.IsCardActivation(t, w);

    /// <summary>
    /// Printed cards with several hard once-per-turn effects say it once, at the end: "You can only use each effect of
    /// "X" once per turn." or, when they share one limit, "You can only use 1 of these effects of "X" per turn, and
    /// only once that turn." Null when each effect keeps its own sentence.
    /// </summary>
    private static string? CombinedLimit(CardProject project, List<EffectEvent> effects)
    {
        var hard = effects.Select((e, i) => (e, i)).Where(x => UsesCombinedLimit(x.e)).ToList();
        if (hard.Count < 2) return null;
        var groups = hard.Select(x => x.e.SharedLimitGroup > 0 ? x.e.SharedLimitGroup : -(x.i + 1)).ToList();
        var name = project.Card.Name;
        if (groups.Distinct().Count() == groups.Count) return $"You can only use each effect of \"{name}\" once per turn.";
        if (groups.Distinct().Count() == 1)
            // "You can only use 1 "X" effect per turn, and only once that turn." when every activated effect shares it.
            return effects.Count(e => e.When is { } w && BlockRegistry.Find(w.BlockId) is { } t && t is not (WhileFaceUpTrigger or SummonProcedureTrigger)) == hard.Count
                ? $"You can only use 1 \"{name}\" effect per turn, and only once that turn."
                : $"You can only use 1 of these effects of \"{name}\" per turn, and only once that turn.";
        return null;
    }

    public static string Describe(EffectEvent effect, CardData card, CardPack? pack = null) => Describe(effect, card, pack, limitSentence: true);

    /// <summary>The name of the card being described, for "except "Card Name"".</summary>
    [ThreadStatic] private static string? _cardName;
    [ThreadStatic] private static CardData? _card;

    private static string Describe(EffectEvent effect, CardData card, CardPack? pack, bool limitSentence)
    {
        _cardName = card.Name;
        _card = card;
        if (TriggerSplit.NeedsSplit(effect))
        {
            effect = PackSerializer.Clone(effect);
            TriggerSplit.Normalize(effect);
        }
        if (effect.When is null || BlockRegistry.Find(effect.When.BlockId) is not { } trigger) return "";
        if (trigger is SummonProcedureTrigger) return DescribeProcedure(effect, card, pack);

        bool passive = trigger is WhileFaceUpTrigger;
        bool activation = trigger is ActivateTrigger || DetailedTriggers.IsCardActivation(trigger, effect.When);
        // FLIP effects are mandatory unless ticked optional; other triggers are optional unless unticked.
        var optionalValue = effect.When.Values.GetValueOrDefault("optional");
        bool optional = trigger is FlipTrigger
            ? string.Equals(optionalValue, "true", StringComparison.OrdinalIgnoreCase)
            : trigger is not (ActivateTrigger or WhileFaceUpTrigger) && !activation && !string.Equals(optionalValue, "false", StringComparison.OrdinalIgnoreCase);

        var head = Head(trigger, effect, pack, passive);
        if (HeadLimit(effect) is { } once && !passive)
            head = head.Length == 0 ? once : head.StartsWith('(') ? once + " " + head : once + ", " + SentenceCase.Lower(head);

        // Lasting effects with nothing before them read as separate sentences, like "Neither player can ... Negate all ...".
        var (targetClause, actions) = Actions(effect.Do, pack, effect, sentences: passive && head.Length == 0);
        string text;
        // "An Xyz Monster whose original Type is Machine and has this card as material gains this effect.\n ● This card ...".
        if (passive && WhileFaceUpTrigger.ForXyz(effect.When))
        {
            var lead = WhileFaceUpTrigger.XyzLead(new BlockArgs(trigger, effect.When, pack) { Effect = effect, CardName = card.Name }, effect.Do.Count > 1);
            text = $"{lead}.{OptionLine}{(head.Length == 0 ? SentenceCase.Upper(actions) : $"{SentenceCase.Upper(head)}, {actions}")}";
        }
        else if (passive)
        {
            text = head.Length == 0 ? SentenceCase.Upper(actions) : $"{SentenceCase.Upper(head)}, {actions}";
            // "If this card attacks, ... until the end of the Damage Step."
            foreach (var b in effect.If)
                if (BlockRegistry.Find(b.BlockId) is ConditionBlock cd && cd.Until(new BlockArgs(cd, b, pack) { Effect = effect, CardName = card.Name }) is { Length: > 0 } until)
                    text += " " + until;
        }
        // "Toss a coin. ● Heads: ... ● Tails: ...": the results of a coin toss or die roll, each on its own line.
        else if (Chance.HasResultOptions(effect.Do)) text = ResultOptionsText(effect, pack, head, optional);
        else if (SplitOptions(effect.Do) is { Count: > 1 } options)
        {
            // "You can discard this card, then activate 1 of these effects; ● Draw 2 cards. ● Destroy 1 card."
            var costs = string.Join(" and ", effect.Cost.Select(b => Text(b, pack, effect)).Where(t => t.Length > 0));
            // "Target 1 face-down monster your opponent controls; your opponent chooses ...": the targets the options share.
            var shared = ChooseEffectsAction.Shared(effect.Do).Select(b => Text(b, pack, effect)).Where(t => t.Length > 0).ToList();
            var target = shared.Count == 0 ? null : string.Join(" and ", shared);
            if (ChooseEffectsAction.Style(effect.Do) == "instead") text = InsteadText(effect, pack, head, optional, costs, target, options);
            else
            {
                var body = costs.Length == 0 ? "activate 1 of these effects" : $"{costs}, then activate 1 of these effects";
                body = optional ? "You can " + body : SentenceCase.Upper(body);
                // "Apply 1 of these effects.", "your opponent chooses 1 of these effects for you to apply." (Selections.cs).
                body = ChooseEffectsAction.Lead(effect, costs, target, optional, card.Name) ?? body + ";";
                text = head.Length == 0 ? body : $"{SentenceCase.Upper(head)}: {body}";
                // Each option on its own line, as printed cards list them.
                text += string.Concat(options.Select(o => OptionLine + OptionText(o, pack, effect))).TrimEnd('.');
            }
        }
        // "Activate this card by sending 1 ... to the GY.": a Continuous Spell/Trap whose activation only has a cost.
        else if (activation && effect.Do.Count == 0 && effect.Cost.Count > 0)
        {
            var by = string.Join(" and ", effect.Cost.Select(b => Gerund(Text(b, pack, effect))).Where(t => t.Length > 0));
            var body = $"Activate this card by {by}";
            text = head.Length == 0 ? body : $"{SentenceCase.Upper(head)}: {body}";
        }
        else
        {
            var before = Before(effect.Cost, pack, effect, targetClause);
            var body = before.Length == 0 ? actions : $"{before}; {actions}";
            // "You can" once, even when the first action is itself optional.
            if (optional && body.StartsWith("you can ", StringComparison.Ordinal)) body = body["you can ".Length..];
            // Murakumo: "You can activate this effect; your opponent can discard 1 card, or that effect is negated."
            if (optional && before.Length == 0 && (body.StartsWith("your opponent can ", StringComparison.Ordinal) || body.StartsWith("your opponent ", StringComparison.Ordinal)))
                body = "activate this effect; " + body;
            // "You can activate this effect; this card gains 500 ATK": a first step that names what changes, not a verb.
            else if (optional && before.Length == 0 && StartsWithSubject(body))
                body = "activate this effect; " + body;
            body = optional ? "You can " + body : SentenceCase.Upper(body);
            text = head.Length == 0 ? body : $"{SentenceCase.Upper(head)}: {body}";
        }
        // "(This card is always treated as a "HERO" card.)" already ends its sentence.
        text = text.TrimEnd();
        text = DeclarePolish(effect, text.EndsWith(".)", StringComparison.Ordinal) ? text : text + ".");
        // A list after the effect ("● Add a card from the Deck to the hand.") starts its own lines.
        foreach (var b in new[] { effect.When! }.Concat(effect.Cost).Concat(effect.Do).Concat(effect.If))
            if (BlockRegistry.Find(b.BlockId) is { } bd && bd.Afterword(new BlockArgs(bd, b, pack) { Effect = effect, CardName = card.Name }) is { Length: > 0 } after)
                text += (after.StartsWith('\n') ? "" : " ") + after;

        // "Tribute this card instead" is a lasting effect that still takes "once per turn".
        var limited = !passive || effect.Do.Any(b => b.BlockId == ReplaceDestroyPassive.BlockId);
        if (effect.Limit == OncePerTurn.Hard && limited && limitSentence)
            text += activation ? " " + ActivationLimit(effect, card.Name) : NameLimit(effect, card.Name, "turn");
        // "Once per turn: ... You can only use this effect of "X" thrice per turn.", "Once while face-up on the field, ...
        // You can only use this effect of "X" once per turn.": each copy's limit before, the name's after.
        if (effect.Limit is OncePerTurn.SoftAndHard or OncePerTurn.FaceUpAndHard && limited)
            text += NameLimit(effect, card.Name, "turn");
        if (effect.Limit == OncePerTurn.Chain && limited)
            text += $" You can only activate this effect of \"{card.Name}\" once per Chain.";
        if (effect.Limit == OncePerTurn.Duel && limited && (limitSentence || !activation))
            text += activation ? " " + ActivationLimit(effect, card.Name) : NameLimit(effect, card.Name, "Duel");
        return text;
    }

    /// <summary>The DO blocks split into the options of "Activate 1 of these effects" (one list when there are none).</summary>
    private static List<List<BlockInstance>> SplitOptions(List<BlockInstance> blocks)
    {
        var options = new List<List<BlockInstance>> { new() };
        foreach (var (b, i) in ChooseEffectsAction.Options(blocks).Select((b, i) => (b, i)))
        {
            if (i > 0 && b.Link == ActionLink.Option) options.Add([]);
            options[^1].Add(b);
        }
        return options;
    }

    /// <summary>One option's text: "Target 1 monster your opponent controls; destroy it."</summary>
    private static string OptionText(List<BlockInstance> blocks, CardPack? pack, EffectEvent effect)
    {
        // "● When an opponent's monster declares an attack while they control more monsters than you do: Negate the attack.":
        // the option's own timing, and what it needs.
        if (blocks.Count > 1 && ChooseEffectsAction.IsOptionTrigger(effect.Do, blocks[0]))
        {
            var when = Text(blocks[0], pack, effect);
            var needs = blocks.Skip(1).TakeWhile(b => ChooseEffectsAction.IsOptionCondition(effect.Do, b)).ToList();
            var also = string.Join(" and ", needs.Select(b => Text(b, pack, effect)).Where(t => t.Length > 0));
            if (when.Contains("opponent", StringComparison.Ordinal))
                also = also.Replace("your opponent controls", "they control", StringComparison.Ordinal).Replace("your opponent has", "they have", StringComparison.Ordinal);
            if (also.Length > 0) when += also.StartsWith("if ", StringComparison.Ordinal) ? ", " + also : " while " + also;
            return $"{SentenceCase.Upper(when)}: {OptionText(blocks.Skip(1 + needs.Count).ToList(), pack, effect)}";
        }
        // "● Field: Target 1 card on the field; ...": the option for where this card was sent from.
        if (blocks.Count > 1 && blocks[0].BlockId == SentFromCondition.BlockId && ChooseEffectsAction.Style(effect.Do) == "sent_from")
            return $"{SentFromCondition.Label(blocks[0])}: {OptionText(blocks.Skip(1).ToList(), pack, effect)}";
        // "● If you control no monsters: Special Summon ...": an option's own condition under a "Choose 1 of these effects" block.
        if (blocks.Count > 1 && ChooseEffectsAction.IsOptionCondition(effect.Do, blocks[0]))
        {
            var rest = OptionText(blocks.Skip(1).ToList(), pack, effect);
            // "● If "The Winged Dragon of Ra" is on the field, take control of ...": in sequence, a part applied only if it holds.
            return ChooseEffectsAction.Style(effect.Do) == "sequence"
                ? $"If {Text(blocks[0], pack, effect)}, {SentenceCase.Lower(rest)}"
                : $"If {Text(blocks[0], pack, effect)}: {rest}";
        }
        // "● Discard 1 card, then target 1 monster your opponent controls; destroy it.": the option's own cost first.
        var paid = blocks.TakeWhile(b => BlockRegistry.Find(b.BlockId) is { Kind: BlockKind.Cost }).ToList();
        var (target, actions) = Actions(blocks.Skip(paid.Count).ToList(), pack, effect);
        var before = Before(paid, pack, effect, target);
        return SentenceCase.Upper(before.Length == 0 ? actions : $"{before}; {actions}") + ".";
    }

    /// <summary>
    /// What goes before the semicolon: the costs, then the targets ("discard 1 card, then target 1 monster"). A cost paid for
    /// each target is said with them: "target face-up cards on the field ... and pay 800 LP for each target" (Ultimate Sky).
    /// </summary>
    private static string Before(IEnumerable<BlockInstance> costBlocks, CardPack? pack, EffectEvent effect, string? target)
    {
        var blocks = costBlocks.ToList();
        var costs = string.Join(" and ", blocks.Where(b => target is null || !PayLifePointsCost.PerTarget(b)).Select(b => Text(b, pack, effect)).Where(t => t.Length > 0));
        var perTarget = target is null ? "" : string.Join(" and ", blocks.Where(PayLifePointsCost.PerTarget).Select(b => Text(b, pack, effect)));
        if (target is not null && perTarget.Length > 0) target = $"{target} and {perTarget}";
        return target is null ? costs : costs.Length == 0 ? target : $"{costs}, then {target}";
    }

    /// <summary>
    /// "It loses 1000 ATK, or, if this card was Special Summoned by the effect of a Synchro Monster, you can destroy that
    /// monster instead": the options in one sentence, each after the first with what it needs. Options that each target
    /// for the same action say the action once: "target 1 Level 2 monster in your GY, or, if your opponent controls a
    /// monster, you can target 1 Rank/Link 2 monster instead; Special Summon it".
    /// </summary>
    private static string InsteadText(EffectEvent effect, CardPack? pack, string head, bool optional, string costs, string? shared, List<List<BlockInstance>> options)
    {
        var parts = options.Select(o =>
        {
            var conditional = o.Count > 1 && ChooseEffectsAction.IsOptionCondition(effect.Do, o[0]);
            var (target, actions) = Actions(conditional ? o.Skip(1).ToList() : o, pack, effect);
            return (Condition: conditional ? Text(o[0], pack, effect) : null, Target: target, Actions: actions);
        }).ToList();
        var sameAction = parts.All(p => p.Target is not null) && parts.Select(p => p.Actions).Distinct().Count() == 1;
        var said = parts.Select(p => sameAction ? p.Target! : p.Target is null ? p.Actions : $"{p.Target}; {p.Actions}").ToList();
        // The later options name again what the first one already did: "that monster", "it".
        for (var i = 1; i < said.Count; i++) said[i] = Again(said[i], said[0], effect);
        var body = said[0];
        for (var i = 1; i < said.Count; i++)
        {
            var later = said[i].StartsWith("you can ", StringComparison.Ordinal) ? said[i]["you can ".Length..] : said[i];
            body += parts[i].Condition is { } cond ? $", or, if {cond}, you can {later} instead" : $", or {later}";
        }
        if (sameAction) body += "; " + parts[0].Actions;
        var before = shared is null ? costs : costs.Length == 0 ? shared : $"{costs}, then {shared}";
        if (before.Length > 0) body = $"{before}; {body}";
        if (optional && body.StartsWith("you can ", StringComparison.Ordinal)) body = body["you can ".Length..];
        else if (optional && before.Length == 0 && StartsWithSubject(body)) body = "activate this effect; " + body;
        body = optional ? "You can " + body : SentenceCase.Upper(body);
        return head.Length == 0 ? body : $"{SentenceCase.Upper(head)}: {body}";
    }

    /// <summary>
    /// A later option of "..., or, if ..., you can ... instead" says the cards the first one is about as card text does:
    /// "that monster" for the shared target, "it" for this card when the first option already said "this card".
    /// </summary>
    private static string Again(string later, string first, EffectEvent effect)
    {
        if (ChooseEffectsAction.Shared(effect.Do).FirstOrDefault() is { } t && t.Filters.TryGetValue("cards", out var f) && Regex.IsMatch(later, @"\bit\b"))
        {
            var monster = f.CardType == FilterCardType.Monster || f.Zones == Zone.MonsterZone;
            later = Regex.Replace(later, @"\bit\b", monster ? "that monster" : "that card");
        }
        if (first.Contains("this card", StringComparison.Ordinal)) later = Regex.Replace(later, @"\bthis card( from your (hand|GY))?", "it");
        return later;
    }

    /// <summary>The text of each option of "Activate 1 of these effects", for the choice the player is shown; empty when there are none.</summary>
    public static IReadOnlyList<string> OptionTexts(EffectEvent effect, CardData card, CardPack? pack = null)
    {
        _cardName = card.Name;
        _card = card;
        if (effect.When?.BlockId == "when.while_face_up" || Chance.HasResultOptions(effect.Do)) return [];
        var options = SplitOptions(effect.Do);
        if (options.Count <= 1) return [];
        var texts = options.Select(o => OptionText(o, pack, effect)).ToList();
        // "Activate 1 or both of these effects": the game also lists both.
        if (ChooseEffectsAction.ChoosesBoth(effect) && texts.Count == 2) texts.Add(ChooseEffectsAction.BothText);
        return texts;
    }

    /// <summary>
    /// "Cannot be Normal Summoned/Set. Must first be Special Summoned (from your hand) by banishing 1 card from your GY."
    /// or, for monsters that can also be Normal Summoned, "If ..., you can Special Summon this card (from your hand)."
    /// </summary>
    private static string DescribeProcedure(EffectEvent effect, CardData card, CardPack? pack)
    {
        var from = SummonProcedureTrigger.FromText(effect.When!.Values.GetValueOrDefault("zone") ?? "hand");
        if (effect.When!.Values.GetValueOrDefault("to") == "center") from += " to your center Main Monster Zone";
        if (effect.When!.Values.GetValueOrDefault("position") is "attack" or "defense" && BlockRegistry.Find(SummonProcedureTrigger.BlockId) is { } proc)
            from += " " + new BlockArgs(proc, effect.When).Describe("position");
        var costs = string.Join(" and ", effect.Cost.Select(b => Gerund(Text(b, pack))).Where(t => t.Length > 0));
        var cond = string.Join(" and ", effect.If.Select(b => Text(b, pack)).Where(t => t.Length > 0));
        var by = costs.Length == 0 ? "" : " by " + costs;

        string text;
        if (IsExtraDeckProcedure(effect))
        {
            // Snake-Eyes Doomed Dragon: "Must be either Fusion Summoned, or Special Summoned by sending 2 face-up Monster Cards ..."
            text = card.SummonRule == SummonRule.ProcedureOnly
                ? $"Must be either {card.Frame} Summoned, or Special Summoned{(by.Length > 0 ? by : " " + from)}"
                : $"Must first be either {card.Frame} Summoned, or Special Summoned from your Extra Deck{by}";
            if (cond.Length > 0) text += (by.Length > 0 ? ", " : " ") + Clause(cond, "while");
            text += ".";
        }
        else if (card.SummonRule == SummonRule.Normal)
        {
            var body = $"you can Special Summon this card {from}{by}";
            text = cond.Length == 0 ? SentenceCase.Upper(body) : $"{SentenceCase.Upper(Clause(cond, "if"))}, {body}";
            text += ".";
        }
        else
        {
            var must = card.SummonRule == SummonRule.ProcedureOnly ? "Must be" : "Must first be";
            text = $"Cannot be Normal Summoned/Set. {must} Special Summoned {from}{by}";
            if (cond.Length > 0) text += (by.Length > 0 ? ", " : " ") + Clause(cond, "while");
            text += card.SummonRule == SummonRule.ProcedureOnly ? ", and cannot be Special Summoned by other ways." : ".";
        }

        // "If Summoned this way, you cannot Special Summon from the Extra Deck for the rest of this turn, except ..."
        foreach (var b in effect.Cost)
            if (BlockRegistry.Find(b.BlockId) is { } bd && bd.Afterword(new BlockArgs(bd, b, pack) { Effect = effect, CardName = card.Name }) is { Length: > 0 } after)
                text += " " + after;

        var times = TimesWord(ScriptCompiler.Times(effect));
        if (effect.Limit == OncePerTurn.Duel)
            text += $" You can only Special Summon \"{card.Name}\" {times} per Duel this way.";
        else if (effect.Limit == OncePerTurn.Hard)
            text += $" You can only Special Summon \"{card.Name}\" {times} per turn this way.";
        else if (effect.Limit == OncePerTurn.Soft)
            text += $" You can only Special Summon this card {times} per turn this way.";
        return text;
    }

    /// <summary>An Extra Deck monster's own way to Special Summon itself "from your Extra Deck", besides its usual Summon.</summary>
    private static bool IsExtraDeckProcedure(EffectEvent e) =>
        e.When?.BlockId == SummonProcedureTrigger.BlockId && e.When.Values.GetValueOrDefault("zone") == "extra";

    /// <summary>"discard 1 card" becomes "discarding 1 card"; "Tribute 1 monster" becomes "Tributing 1 monster".</summary>
    private static string Gerund(string phrase)
    {
        if (phrase.Length == 0) return phrase;
        var space = phrase.IndexOf(' ');
        var verb = space < 0 ? phrase : phrase[..space];
        var rest = space < 0 ? "" : phrase[space..];
        var ing = verb.EndsWith('e') && !verb.EndsWith("ee", StringComparison.Ordinal) ? verb[..^1] + "ing" : verb + "ing";
        if (ing.Equals("tributing", StringComparison.OrdinalIgnoreCase)) ing = "Tributing";
        return ing + rest;
    }

    /// <summary>The part before the colon: trigger wording plus IF conditions.</summary>
    private static string Head(BlockDefinition trigger, EffectEvent effect, CardPack? pack, bool passive)
    {
        // "If this card is Normal or Special Summoned, or if this card is Tributed".
        var when = effect.AlsoWhen.Count > 0 && Combined(effect, pack) is { } combined ? combined : Text(effect.When!, pack);
        if (effect.AlsoWhen.Count > 0 && Combined(effect, pack) is null)
            foreach (var also in effect.AlsoWhen.Select(b => Text(b, pack)).Where(t => t.Length > 0))
                when = when.Length == 0 ? also : $"{when}, or {SentenceCase.Lower(also)}";
        // S:P Little Knight: "If this card is Link Summoned using a Fusion, Synchro, Xyz, or Link Monster as material".
        var ifs = effect.If;
        if (effect.AlsoWhen.Count == 0 && effect.When!.BlockId == SummonedTrigger.BlockId && ifs.FirstOrDefault(b => b.BlockId == SummonedUsingCondition.BlockId) is { } usedMats
            && BlockRegistry.Find(usedMats.BlockId) is { } usedDef)
        {
            when += " " + SummonedUsingCondition.Using(new BlockArgs(usedDef, usedMats, pack));
            ifs = ifs.Where(b => !ReferenceEquals(b, usedMats)).ToList();
        }
        // Stargazer Magician: "Unless you have a "Magician" card in your other Pendulum Zone, this card's Pendulum Scale becomes 4."
        if (passive && effect.AlsoWhen.Count == 0 && ifs is [{ BlockId: "if.no_cards" } none] && effect.Do.Any(b => b.BlockId == PendulumScalePassive.BlockId)
            && none.Filters.TryGetValue("cards", out var unless) && unless.Owner == CardOwner.You && (unless.Zones & Zone.Field) == 0 && unless.Zones != Zone.None)
        {
            var noun = FilterText.Describe(unless, pack, preposition: "in");
            return $"unless you have {SummonWording.Article(noun)} {noun}";
        }
        var conditions = ifs.Select(b => Text(b, pack)).Where(t => t.Length > 0).ToList();
        // "During the Main Phase, if ...": the turn and phase come first (TimingFirst).
        var during = During(conditions);
        // "While you have no cards in your hand, if this card attacks": a clause that brings its own "if" comes last.
        var own = conditions.Where(t => t.StartsWith("if ", StringComparison.Ordinal)).ToList();
        // "..., except during the Draw Phase", "..., except during the turn this card was Special Summoned": after the trigger.
        var except = conditions.Where(t => t.StartsWith("except ", StringComparison.Ordinal)).ToList();
        var cond = string.Join(" and ", conditions.Except(own).Except(during).Except(except));
        if (own.Count > 0) cond = cond.Length == 0 ? string.Join(", ", own) : $"{cond}, {string.Join(", ", own)}";

        // Where this card must be, when no cost or action on this card says it: "If this card is in your GY".
        if (trigger is QuickTrigger or IgnitionTrigger or EventTrigger or CardsEventTrigger or PhaseTrigger or ActivationTrigger or AttackDeclaredTrigger
                or BattleWindowTrigger or SummonAttemptTrigger
            && effect.When!.Values.GetValueOrDefault("zone") is { } zone && Zones.Where(zone) is { } where && !SaysWhereThisCardIs(effect))
            cond = cond.Length == 0 ? $"this card is {where}" : $"this card is {where} and {cond}";
        // "If your opponent controls a card, you can activate this card from your hand": a rule, not a lasting "while".
        var joiner = when.Length > 0 || passive && !effect.Do.All(b => b.BlockId is ActivateFromHandPassive.BlockId or NoTributeSummonPassive.BlockId or SelfDestroyPassive.BlockId or ActivateSetTurnPassive.BlockId or CannotBeSummonedPassive.BlockId) ? "while" : "if";
        cond = TimingFirst(during, cond, joiner);

        // "When your opponent Normal Summons a monster during their turn", not "during your opponent's turn".
        if (trigger is QuickTrigger or ActivateTrigger && Timing.IsOpponents(effect.When!.Values.GetValueOrDefault("timing") ?? ""))
            cond = cond.Replace("during your opponent's turn", "during their turn", StringComparison.Ordinal);

        string head;
        if (cond.Length == 0) head = when;
        else if (when.Length == 0) head = Clause(cond, joiner);
        // "During the End Phase, if ...", "At the end of the Battle Phase, if ...", as printed cards say it.
        else if (trigger is PhaseTrigger) head = $"{when}, {Clause(cond, "if")}";
        else head = $"{when} {Clause(cond, joiner)}";
        if (except.Count > 0) head = head.Length == 0 ? string.Join(", ", except) : $"{head}, {string.Join(", ", except)}";
        // "During your Draw Phase, if this card is in your GY, instead of conducting your normal draw".
        if (PhaseTrigger.InsteadOfDraw(effect.When!)) head += ", instead of conducting your normal draw";

        // "During your opponent's Main Phase", not "During the Main Phase during your opponent's turn".
        head = PhaseTurn().Replace(head, m => $"{m.Groups[1].Value} {(m.Groups[3].Value == "your opponent's" ? "your opponent's" : "your")} {m.Groups[2].Value} Phase");
        if (trigger is QuickTrigger || DetailedTriggers.IsQuick(trigger, effect.When!)) head = head.Length == 0 ? "(Quick Effect)" : $"{head} (Quick Effect)";
        return head;
    }

    /// <summary>Costs and actions on this card that already say where it is ("discard this card", "Special Summon this card from your hand").</summary>
    private static bool SaysWhereThisCardIs(EffectEvent effect) =>
        effect.Cost.Concat(effect.Do).Any(b => b.BlockId is "cost.this_card" or "cost.reveal_self" or "do.this_card"
                                               // "Special Summon both it and this card" doesn't say where this card is.
                                               || b.BlockId == "do.special_summon_self" && b.Values.GetValueOrDefault("with", "none") != "target"
                                               // "Equip this card to it" doesn't say where this card is either.
                                               || b.BlockId != EquipAction.BlockId && b.Filters.Values.Any(f => f.ThisCard));

    /// <summary>"during your turn" stays as is; anything else gets a joining word such as "if" or "while".</summary>
    private static string Clause(string cond, string joiner) =>
        cond.StartsWith("during ", StringComparison.Ordinal) || cond.StartsWith("if ", StringComparison.Ordinal) ? cond : $"{joiner} {cond}";

    private static (string? Target, string Text) Actions(List<BlockInstance> blocks, CardPack? pack, EffectEvent effect, bool sentences = false)
    {
        string? target = null;
        var sb = new System.Text.StringBuilder();
        BlockDefinition? previous = null;
        // "and if you banish any": the step before says "if you do" in its own words.
        string? ifYouDo = null;
        var previousOnlyIf = false;
        // A declaration says its part before the semicolon too: "declare 1 Attribute, then target 1 face-up monster; ...".
        var declared = false;
        var chanceCases = 0;
        foreach (var b in blocks)
        {
            if (BlockRegistry.Find(b.BlockId) is not { } def) continue;
            var args = new BlockArgs(def, b, pack) { Effect = effect, CardName = _cardName, Card = _card };
            string t;
            if (target is null && def.DescribeTargeting(args) is { } targeted)
            {
                target = Clean(targeted.Target);
                t = Clean(targeted.Action);
                declared = def is DeclareAction;
            }
            else if (target is not null && (declared || def is DeclareAction) && def.DescribeTargeting(args) is { } more)
            {
                target = def is DeclareAction ? $"{target} and {Clean(more.Target)}" : $"{target}, then {Clean(more.Target)}";
                t = Clean(more.Action);
                declared = false;
            }
            else t = Text(b, pack, effect);
            if (t.Length == 0) continue;
            // An IF block among the DO blocks: "then if you control a Synchro Monster, ...".
            if (def is ConditionBlock && !t.StartsWith("if ", StringComparison.Ordinal)) t = "if " + t;
            // "you can Special Summon this card".
            if (b.Optional && def is ActionBlock && !t.StartsWith("you can ", StringComparison.Ordinal)) t = "you can " + t;
            // A floodgate is its own sentence: "Spell Cards ... cannot be activated. Negate all Spell effects on the field."
            var gate = def is LockPassive or NegateAllPassive or BanishInsteadPassive or ToonRulesPassive;
            var wasGate = previous is LockPassive or NegateAllPassive or BanishInsteadPassive or ToonRulesPassive;
            // "toss a coin and if the result is heads, ..., or if the result is tails, ...".
            if (ChanceJoiner(b, previous, ref chanceCases) is { } chanceJoin && sb.Length > 0)
            {
                sb.Append(chanceJoin).Append(chanceJoin.StartsWith('.') ? SentenceCase.Upper(t) : t);
                previous = def;
                previousOnlyIf = true;
                continue;
            }
            previous = def;
            var ownIfYouDo = ifYouDo;
            ifYouDo = def.IfYouDoText(args);
            // "..., then if you have a Trap in your GY, destroy that card".
            if (sb.Length > 0 && previousOnlyIf)
            {
                sb.Append(", ").Append(t);
                previousOnlyIf = def is OnlyIfAction or ConditionBlock;
                continue;
            }
            previousOnlyIf = def is OnlyIfAction or ConditionBlock;
            if (sb.Length > 0 && sentences && (gate || wasGate))
            {
                sb.Append(". ").Append(SentenceCase.Upper(t));
                continue;
            }
            // "Special Summon 1 monster from your GY, but destroy it during the End Phase."
            if (sb.Length > 0 && def is EndPhaseAction) { sb.Append(", but ").Append(t); continue; }
            if (sb.Length > 0)
            {
                sb.Append(b.Link switch
                {
                    ActionLink.IfYouDo => $", and {ownIfYouDo ?? "if you do"}, ",
                    ActionLink.Then => ", then ",
                    ActionLink.AlsoAfterThat => ". Also, after that, ",
                    ActionLink.IfYouDont => ", and if you don't, ",
                    // "..., also you cannot Special Summon ... for the rest of this turn": a lock is added with "also".
                    _ when def is SummonRestrictionAction => ", also ",
                    // Therion "King" Regulus: "... gains 700 ATK, also it can activate the 2nd effect listed above ...".
                    _ when def is GrantEffectPassive && t.StartsWith("it ", StringComparison.Ordinal) => ", also ",
                    _ => ", and ",
                });
            }
            sb.Append(t);
        }
        return (target, sb.ToString());
    }

    private static string Text(BlockInstance b, CardPack? pack, EffectEvent? effect = null)
    {
        var def = BlockRegistry.Find(b.BlockId);
        return def is null ? "" : SentenceCase.Lower(Clean(def.Describe(new BlockArgs(def, b, pack) { Effect = effect, CardName = _cardName, Card = _card })));
    }

    /// <summary>
    /// The WHEN blocks of an effect read together: events that share wording share it ("this card is Normal or Special
    /// Summoned"), the rest follow with ", or if" ("..., or if this card is Tributed"). Null when one doesn't combine.
    /// </summary>
    private static string? Combined(EffectEvent effect, CardPack? pack)
    {
        var groups = new List<(string Lead, string Template, List<string> Labels, string? Alone)>();
        foreach (var b in new[] { effect.When! }.Concat(effect.AlsoWhen))
        {
            if (BlockRegistry.Find(b.BlockId) is not { } def) return null;
            if (def.Phrase(new BlockArgs(def, b, pack) { Effect = effect, CardName = _cardName }) is not { } phrase) return null;
            var at = groups.FindIndex(g => g.Lead == phrase.Lead && g.Template == phrase.Template);
            if (at < 0) groups.Add((phrase.Lead, phrase.Template, [phrase.Label], phrase.Alone));
            else if (!groups[at].Labels.Contains(phrase.Label)) groups[at].Labels.Add(phrase.Label);
        }
        var clauses = groups.Select(g =>
        {
            var labels = SummonOrder(g.Labels.ToList());
            // "destroyed by battle or card effect".
            var battle = labels.IndexOf("destroyed by battle");
            if (battle >= 0 && labels.Remove("destroyed by a card effect")) labels[labels.IndexOf("destroyed by battle")] = "destroyed by battle or card effect";
            if (labels.Count == 1 && g.Alone is { } alone) return (g.Lead, Text: alone);
            // "from the hand or GY": "the" once.
            if (labels.Count > 1 && labels[0].StartsWith("the ", StringComparison.Ordinal))
                labels = [labels[0], .. labels.Skip(1).Select(l => l.StartsWith("the ", StringComparison.Ordinal) ? l[4..] : l)];
            return (g.Lead, Text: g.Template.Replace("{0}", OrList(labels), StringComparison.Ordinal));
        }).ToList();
        var sentence = clauses[0].Lead + " " + clauses[0].Text
            + string.Concat(clauses.Skip(1).Select(c => $", or {c.Lead.ToLowerInvariant()} {c.Text}"));
        return SentenceCase.Lower(Clean(sentence));
    }

    /// <summary>"A or B", "A, B, or C".</summary>
    private static string OrList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} or {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", or " + items[^1],
    };

    /// <summary>Collapses the double spaces left by empty blanks.</summary>
    private static string Clean(string s) => Spaces().Replace(s, " ").Replace(" ,", ",").Trim();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b([Dd]uring) the (Main|Standby|Battle|End) Phase,? during (your opponent's|your) turn")]
    private static partial Regex PhaseTurn();
}

/// <summary>Capitalisation that never lowercases card terms like "Dragon", "GY" or "Special Summon".</summary>
public static class SentenceCase
{
    private static readonly HashSet<string> OrdinaryWords = new(StringComparer.Ordinal)
    {
        "Add", "Activate", "Banish", "Destroy", "Discard", "Draw", "During", "Gain", "Gains", "If", "Inflict", "It",
        "Pay", "Return", "Send", "Shuffle", "There", "This", "While", "You", "Your", "When", "Once", "Place", "Reveal", "Take", "Target", "Change", "Look", "Excavate", "Detach", "Attach", "Equip", "Negate",
        "At",
    };

    /// <summary>Lowercases the first letter only when the first word is an ordinary word.</summary>
    public static string Lower(string s)
    {
        if (s.Length == 0) return s;
        var space = s.IndexOf(' ');
        var first = space < 0 ? s : s[..space];
        return OrdinaryWords.Contains(first) ? char.ToLowerInvariant(s[0]) + s[1..] : s;
    }

    public static string Upper(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
