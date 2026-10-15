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
    /// <summary>The engine constant for a monster kind, and whether the card must lack it ("non-Tuner").</summary>
    public static (string Type, bool Not)? KindType(MonsterKind kind) => kind switch
    {
        MonsterKind.Effect => ("TYPE_EFFECT", false),
        MonsterKind.NonEffect => ("TYPE_EFFECT", true),
        MonsterKind.Normal => ("TYPE_NORMAL", false),
        MonsterKind.Tuner => ("TYPE_TUNER", false),
        MonsterKind.NonTuner => ("TYPE_TUNER", true),
        MonsterKind.Fusion => ("TYPE_FUSION", false),
        MonsterKind.Synchro => ("TYPE_SYNCHRO", false),
        MonsterKind.Xyz => ("TYPE_XYZ", false),
        MonsterKind.Link => ("TYPE_LINK", false),
        MonsterKind.Ritual => ("TYPE_RITUAL", false),
        MonsterKind.Pendulum => ("TYPE_PENDULUM", false),
        MonsterKind.Flip => ("TYPE_FLIP", false),
        MonsterKind.Toon => ("TYPE_TOON", false),
        MonsterKind.Spirit => ("TYPE_SPIRIT", false),
        MonsterKind.Union => ("TYPE_UNION", false),
        MonsterKind.Gemini => ("TYPE_DUAL", false),
        MonsterKind.Token => ("TYPE_TOKEN", false),
        MonsterKind.NonToken => ("TYPE_TOKEN", true),
        _ => null,
    };

    /// <summary>
    /// The Lua tests a card must pass, not counting where it is. For summon materials, pass the kind of Summon:
    /// the engine's material checks (IsLinkType, IsFusionSetCard...) count effects that change a material's Type,
    /// Attribute or name for that Summon.
    /// </summary>
    public static List<string> Conditions(CardFilter f, string card = "c", SummonProcedureKind material = SummonProcedureKind.None)
    {
        var parts = new List<string>();
        var (isType, isAttr, isRace, isSet, isCode) = material switch
        {
            SummonProcedureKind.Fusion => ("IsFusionType", "IsFusionAttribute", "IsRace", "IsFusionSetCard", "IsFusionCode"),
            SummonProcedureKind.Link => ("IsLinkType", "IsLinkAttribute", "IsLinkRace", "IsLinkSetCard", "IsLinkCode"),
            SummonProcedureKind.Synchro => ("IsSynchroType", "IsAttribute", "IsRace", "IsSetCard", "IsCode"),
            SummonProcedureKind.Xyz => ("IsXyzType", "IsAttribute", "IsRace", "IsSetCard", "IsCode"),
            _ => ("IsType", "IsAttribute", "IsRace", "IsSetCard", "IsCode"),
        };
        switch (f.CardType)
        {
            case FilterCardType.Monster: parts.Add($"{card}:IsType(TYPE_MONSTER)"); break;
            case FilterCardType.Spell: parts.Add($"{card}:IsType(TYPE_SPELL)"); break;
            case FilterCardType.Trap: parts.Add($"{card}:IsType(TYPE_TRAP)"); break;
        }
        if (KindType(f.MonsterKind) is { } kind)
            parts.Add(kind.Not ? $"not {card}:{isType}({kind.Type})" : $"{card}:{isType}({kind.Type})");
        if (f.CardCode != 0) parts.Add($"{card}:{isCode}({Lua.Num(f.CardCode)})");
        if (f.ExceptOwnName) parts.Add($"not {card}:{isCode}(id)");
        if (f.ExceptCode != 0) parts.Add($"not {card}:{isCode}({Lua.Num(f.ExceptCode)})");
        if (f.Archetype != 0) parts.Add($"{card}:{isSet}({Lua.Hex(f.Archetype)})");
        if (f.Race != CardRace.None) parts.Add($"{card}:{isRace}({EnumNames.LuaConstant(f.Race)})");
        if (f.Attribute != CardAttribute.None) parts.Add($"{card}:{isAttr}({EnumNames.LuaConstant(f.Attribute)})");
        // Xyz Monsters have a Rank, not a Level.
        var lv = f.MonsterKind == MonsterKind.Xyz ? "Rank" : "Level";
        if (f.LevelMin is { } lmin && f.LevelMax == lmin) parts.Add($"{card}:Is{lv}({lmin})");
        else
        {
            if (f.LevelMin is { } lmin2) parts.Add($"{card}:Is{lv}Above({lmin2})");
            if (f.LevelMax is { } lmax) parts.Add($"{card}:Is{lv}Below({lmax})");
        }
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
