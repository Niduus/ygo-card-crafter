using CardCrafter.Core.Model;

namespace CardCrafter.App.ViewModels;

/// <summary>The drop-down lists used across the editor.</summary>
public static class Lists
{
    public static readonly IReadOnlyList<Option<CardKind>> Kinds =
        Options.Of((CardKind.Monster, "Monster"), (CardKind.Spell, "Spell"), (CardKind.Trap, "Trap"));

    public static readonly IReadOnlyList<Option<MonsterFrame>> Frames = Options.Of(
        (MonsterFrame.Normal, "Normal"), (MonsterFrame.Effect, "Effect"), (MonsterFrame.Ritual, "Ritual"),
        (MonsterFrame.Fusion, "Fusion"), (MonsterFrame.Synchro, "Synchro"), (MonsterFrame.Xyz, "Xyz"), (MonsterFrame.Link, "Link"));

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
        (OncePerTurn.Hard, "Once per turn (by card name)"));

    public static readonly IReadOnlyList<Option<ActionLink>> Links = Options.Of(
        (ActionLink.And, "and"),
        (ActionLink.IfYouDo, "and if you do"),
        (ActionLink.Then, "then"),
        (ActionLink.AlsoAfterThat, "also, after that"));

    public static readonly IReadOnlyList<Option<CardOwner>> Owners = Options.Of(
        (CardOwner.You, "Yours"), (CardOwner.Opponent, "Your opponent's"), (CardOwner.Either, "Either player's"));

    public static readonly IReadOnlyList<Option<FilterCardType>> FilterTypes = Options.Of(
        (FilterCardType.Any, "Any card"), (FilterCardType.Monster, "Monster"), (FilterCardType.Spell, "Spell"), (FilterCardType.Trap, "Trap"));
}
