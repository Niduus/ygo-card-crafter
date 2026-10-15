using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>A compiled card filter: the Lua function plus the arguments every engine search call needs.</summary>
public sealed class FilterRef
{
    public required string Function { get; init; }
    public required string Self { get; init; }
    public required string Opp { get; init; }
    /// <summary>"e:GetHandler()" when this card is excluded, otherwise "nil".</summary>
    public required string Exclude { get; init; }
    public required CardFilter Filter { get; init; }

    /// <summary>The function wrapped so cards in a GY under Necrovalley-like effects are skipped.</summary>
    public string ForSelection => Filter.Zones.HasFlag(Zone.Graveyard) ? $"aux.NecroValleyFilter({Function})" : Function;

    /// <summary>The player argument for SetOperationInfo.</summary>
    public string InfoPlayer => Lua.Player(Filter.Owner);

    public string InfoLocation => Lua.Locations(Filter.Zones);

    /// <summary>The check used in a target function's "chkc" branch for a targeting effect.</summary>
    public string ChkcCheck
    {
        get
        {
            var parts = new List<string> { $"chkc:IsLocation({Lua.Locations(Filter.Zones)})" };
            if (Filter.Owner == CardOwner.You) parts.Add("chkc:IsControler(tp)");
            if (Filter.Owner == CardOwner.Opponent) parts.Add("chkc:IsControler(1-tp)");
            parts.Add($"{Function}(chkc,e,tp)");
            if (Filter.ExcludeThisCard) parts.Add("chkc~=e:GetHandler()");
            return string.Join(" and ", parts);
        }
    }
}

public static class FilterCompiler
{
    /// <summary>The Lua tests a card must pass, not counting where it is.</summary>
    public static List<string> Conditions(CardFilter f, string card = "c")
    {
        var parts = new List<string>();
        switch (f.CardType)
        {
            case FilterCardType.Monster: parts.Add($"{card}:IsType(TYPE_MONSTER)"); break;
            case FilterCardType.Spell: parts.Add($"{card}:IsType(TYPE_SPELL)"); break;
            case FilterCardType.Trap: parts.Add($"{card}:IsType(TYPE_TRAP)"); break;
        }
        if (f.CardCode != 0) parts.Add($"{card}:IsCode({Lua.Num(f.CardCode)})");
        if (f.Archetype != 0) parts.Add($"{card}:IsSetCard({Lua.Hex(f.Archetype)})");
        if (f.Race != CardRace.None) parts.Add($"{card}:IsRace({EnumNames.LuaConstant(f.Race)})");
        if (f.Attribute != CardAttribute.None) parts.Add($"{card}:IsAttribute({EnumNames.LuaConstant(f.Attribute)})");
        if (f.LevelMin is { } lmin) parts.Add($"{card}:IsLevelAbove({lmin})");
        if (f.LevelMax is { } lmax) parts.Add($"{card}:IsLevelBelow({lmax})");
        if (f.AtkMin is { } amin) parts.Add($"{card}:IsAttackAbove({amin})");
        if (f.AtkMax is { } amax) parts.Add($"{card}:IsAttackBelow({amax})");
        if (f.PointedToByThisCard) parts.Add($"e:GetHandler():GetLinkedGroup():IsContains({card})");
        if (f.FaceUpOnly)
        {
            var onlyPublicZones = (f.Zones & ~(Zone.Field | Zone.Banished)) == Zone.None;
            parts.Add(onlyPublicZones
                ? $"{card}:IsFaceup()"
                : $"({card}:IsFaceup() or not {card}:IsLocation(LOCATION_ONFIELD+LOCATION_REMOVED))");
        }
        return parts;
    }
}
