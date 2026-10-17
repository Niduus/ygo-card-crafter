using CardCrafter.Core.Model;

namespace CardCrafter.App.ViewModels;

/// <summary>The drop-down lists used across the editor.</summary>
public static class Lists
{
    public static readonly IReadOnlyList<Option<CardKind>> Kinds =
        Options.Of((CardKind.Monster, "Monster"), (CardKind.Spell, "Spell"), (CardKind.Trap, "Trap"));

    /// <summary>Card pools as YGO Omega's deck editor names them.</summary>
    public static readonly IReadOnlyList<Option<CardPool>> Pools = Options.Of(
        (CardPool.Custom, "Custom"), (CardPool.Ocg, "OCG"), (CardPool.Tcg, "TCG"), (CardPool.OcgTcg, "OCG & TCG"));

    public static string PoolName(long ot) => Pools.FirstOrDefault(p => (long)p.Value == ot)?.Label ?? $"ot {ot}";

    public static readonly IReadOnlyList<Option<Limitation>> Limitations = Options.Of(
        (Limitation.Unlimited, "Unlimited"), (Limitation.SemiLimited, "Semi-Limited"), (Limitation.Limited, "Limited"), (Limitation.Forbidden, "Forbidden"));

    public static readonly IReadOnlyList<Option<MonsterFrame>> Frames = Options.Of(
        (MonsterFrame.Normal, "Normal"), (MonsterFrame.Effect, "Effect"), (MonsterFrame.Ritual, "Ritual"),
        (MonsterFrame.Fusion, "Fusion"), (MonsterFrame.Synchro, "Synchro"), (MonsterFrame.Xyz, "Xyz"), (MonsterFrame.Link, "Link"), (MonsterFrame.Token, "Token"));

    public static readonly IReadOnlyList<Option<SpellKind>> SpellKinds = Options.Of(
        (SpellKind.Normal, "Normal"), (SpellKind.QuickPlay, "Quick-Play"), (SpellKind.Continuous, "Continuous"),
        (SpellKind.Equip, "Equip"), (SpellKind.Field, "Field"), (SpellKind.Ritual, "Ritual"));

    public static readonly IReadOnlyList<Option<TrapKind>> TrapKinds = Options.Of(
        (TrapKind.Normal, "Normal"), (TrapKind.Continuous, "Continuous"), (TrapKind.Counter, "Counter"));

    public static readonly IReadOnlyList<Option<CardAttribute>> Attributes =
        Enum.GetValues<CardAttribute>().Where(a => a != CardAttribute.None)
            .Select(a => new Option<CardAttribute>(a, EnumNames.Display(a))).ToList();

    public static readonly IReadOnlyList<Option<CardAttribute>> AttributesOrAny =
        [new(CardAttribute.None, "Any Attribute"), .. Attributes];

    public static readonly IReadOnlyList<Option<CardRace>> Races =
        Enum.GetValues<CardRace>().Where(r => r != CardRace.None)
            .Select(r => new Option<CardRace>(r, EnumNames.Display(r))).OrderBy(o => o.Label).ToList();

    public static readonly IReadOnlyList<Option<CardRace>> RacesOrAny = [new(CardRace.None, "Any Type"), .. Races];

    /// <summary>Standard Types plus the pack's custom Types, and the current value if it is neither.</summary>
    public static List<Option<CardRace>> RacesFor(CardPack pack, CardRace current)
    {
        var list = Races.ToList();
        foreach (var t in pack.CustomTypes.OrderBy(t => t.Name))
            if (list.All(o => (long)o.Value != t.Value)) list.Add(new((CardRace)t.Value, $"{t.Name} (custom)"));
        if (current != CardRace.None && list.All(o => o.Value != current)) list.Add(new(current, pack.TypeName(current)));
        return list;
    }

    public static readonly IReadOnlyList<Option<OncePerTurn>> Limits = Options.Of(
        (OncePerTurn.None, "No limit"),
        (OncePerTurn.Soft, "Once per turn (each copy)"),
        (OncePerTurn.Hard, "Once per turn (by card name)"),
        (OncePerTurn.Duel, "Once per Duel (by card name)"),
        (OncePerTurn.Chain, "Once per Chain"),
        (OncePerTurn.FaceUp, "Once while face-up on the field"),
        (OncePerTurn.FaceUpAndHard, "Once while face-up, and once per turn by name"),
        (OncePerTurn.SoftAndHard, "Once per turn (each copy), and by name"));

    public static readonly IReadOnlyList<Option<ActionLink>> Links = Options.Of(
        (ActionLink.And, "and"),
        (ActionLink.IfYouDo, "and if you do"),
        (ActionLink.Then, "then"),
        (ActionLink.AlsoAfterThat, "also, after that"),
        (ActionLink.IfYouDont, "and if you don't"),
        (ActionLink.Option, "● or instead (another option, or a coin/die result's own line)"));

    public static readonly IReadOnlyList<Option<CardOwner>> Owners = Options.Of(
        (CardOwner.You, "Yours"), (CardOwner.Opponent, "Your opponent's"), (CardOwner.Either, "Either player's"));

    public static readonly IReadOnlyList<Option<MonsterKind>> MonsterKinds = Options.Of(
        (MonsterKind.Any, "Any kind"), (MonsterKind.Effect, "Effect Monster"), (MonsterKind.NonEffect, "non-Effect"),
        (MonsterKind.Normal, "Normal Monster"), (MonsterKind.Tuner, "Tuner"), (MonsterKind.NonTuner, "non-Tuner"),
        (MonsterKind.Fusion, "Fusion Monster"), (MonsterKind.Synchro, "Synchro Monster"), (MonsterKind.Xyz, "Xyz Monster"),
        (MonsterKind.Link, "Link Monster"), (MonsterKind.Ritual, "Ritual Monster"), (MonsterKind.Pendulum, "Pendulum Monster"),
        (MonsterKind.Flip, "Flip monster"), (MonsterKind.Toon, "Toon monster"), (MonsterKind.Spirit, "Spirit monster"),
        (MonsterKind.Union, "Union monster"), (MonsterKind.Gemini, "Gemini monster"), (MonsterKind.Token, "Token"), (MonsterKind.NonToken, "non-Token"));

    public static readonly IReadOnlyList<Option<FilterPosition>> Positions = Options.Of(
        (FilterPosition.Any, "Any position"), (FilterPosition.Attack, "Attack Position"), (FilterPosition.Defense, "Defense Position"),
        (FilterPosition.FaceUpAttack, "face-up Attack Position"), (FilterPosition.FaceUpDefense, "face-up Defense Position"),
        (FilterPosition.FaceDownDefense, "face-down Defense Position"));

    public static readonly IReadOnlyList<Option<SpellTrapKind>> SpellTrapKinds = Options.Of(
        (SpellTrapKind.Any, "Any Spell/Trap kind"), (SpellTrapKind.Normal, "Normal"), (SpellTrapKind.Continuous, "Continuous"),
        (SpellTrapKind.QuickPlay, "Quick-Play"), (SpellTrapKind.Field, "Field"), (SpellTrapKind.Equip, "Equip"), (SpellTrapKind.Ritual, "Ritual"),
        (SpellTrapKind.Counter, "Counter"));

    public static readonly IReadOnlyList<Option<FilterSummoned>> Summoned = Options.Of(
        (FilterSummoned.Any, "However it was Summoned"), (FilterSummoned.Special, "Special Summoned"),
        (FilterSummoned.FromExtra, "Special Summoned from the Extra Deck"), (FilterSummoned.Tribute, "Tribute Summoned"),
        (FilterSummoned.ThisTurn, "Summoned this turn"));

    public static readonly IReadOnlyList<Option<FilterCardType>> FilterTypes = Options.Of(
        (FilterCardType.Any, "Any card"), (FilterCardType.Monster, "Monster"), (FilterCardType.Spell, "Spell"), (FilterCardType.Trap, "Trap"), (FilterCardType.SpellTrap, "Spell/Trap"));
}
