namespace CardCrafter.Core.Model;

/// <summary>
/// Gives every card whose passcode is too large for the game's effect texts (<see cref="PasscodeGenerator.MaxForStrings"/>)
/// a new one that fits, and updates everything in the pack that names those cards: card filters, Fusion Materials,
/// aliases and hand-written scripts.
/// </summary>
public static class PasscodeFix
{
    public static bool TooLarge(long id) => id > PasscodeGenerator.MaxForStrings;

    /// <summary>Old passcode to new one, for each card changed.</summary>
    public static Dictionary<long, long> Apply(CardPack pack, IEnumerable<long>? alsoTaken = null)
    {
        var taken = pack.Cards.Select(c => c.Card.Id).Concat(alsoTaken ?? []).ToList();
        var map = new Dictionary<long, long>();
        foreach (var card in pack.Cards.Where(c => TooLarge(c.Card.Id)))
        {
            var next = PasscodeGenerator.Generate(taken);
            taken.Add(next);
            map[card.Card.Id] = next;
        }
        if (map.Count == 0) return map;
        long New(long code) => map.TryGetValue(code, out var n) ? n : code;
        foreach (var card in pack.Cards)
        {
            var old = card.Card.Id;
            card.Card.Id = New(old);
            card.Card.Alias = New(card.Card.Alias);
            foreach (var f in Filters(card))
            {
                f.CardCode = New(f.CardCode);
                f.AltCodes = f.AltCodes.Select(New).ToArray();
                f.MentionsCode = New(f.MentionsCode);
                f.ExceptCode = New(f.ExceptCode);
            }
            card.Procedure.FusionMaterialCodes = card.Procedure.FusionMaterialCodes.Select(New).ToList();
            if (card.CustomLua.Length > 0)
                foreach (var (from, to) in map)
                    card.CustomLua = Compiler.LuaPieces.Retarget(card.CustomLua, from, to);
        }
        return map;
    }

    /// <summary>Every card filter of a card: its blocks, materials and equip targets.</summary>
    public static IEnumerable<CardFilter> Filters(CardProject card)
    {
        foreach (var e in card.Effects)
            foreach (var b in new[] { e.When }.Concat(e.AlsoWhen).Concat(e.If).Concat(e.Cost).Concat(e.Do))
                if (b is not null)
                    foreach (var f in b.Filters.Values) yield return f;
        var p = card.Procedure;
        yield return p.MaterialFilter;
        yield return p.TunerFilter;
        yield return p.IncludeFilter;
        yield return p.XyzAlternateFilter;
        foreach (var slot in p.Materials)
        {
            yield return slot.Filter;
            if (slot.Or is not null) yield return slot.Or;
        }
        yield return card.EquipFilter;
    }
}
