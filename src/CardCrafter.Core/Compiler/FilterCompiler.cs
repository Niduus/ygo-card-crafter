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
    /// <param name="hasEffect">The function has the effect and its player (e, tp), for requirements that need them.</param>
    public static List<string> Conditions(CardFilter f, string card = "c", SummonProcedureKind material = SummonProcedureKind.None, bool hasEffect = true)
    {
        // "1 "Ancient Gear" monster or 1 "Geartown"": either kind, each with what this filter says about where it is.
        if (f.OrCards is { } alt && !f.IsSpecific())
        {
            var main = f.Clone();
            main.OrCards = null;
            var one = Conditions(main, card, material, hasEffect);
            var two = Conditions(Alternative(f, alt), card, material, hasEffect);
            if (one.Count == 0 || two.Count == 0) return [];
            return ["((" + string.Join(" and ", one) + ") or (" + string.Join(" and ", two) + "))"];
        }
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
            // "Monster Cards": also the monsters placed in the Spell & Trap Zone as Continuous Spells, as official cards check them.
            case FilterCardType.Monster when f.MonsterCards && material == SummonProcedureKind.None: parts.Add($"{card}:GetOriginalType()&TYPE_MONSTER~=0"); break;
            case FilterCardType.Monster: parts.Add($"{card}:IsType(TYPE_MONSTER)"); break;
            case FilterCardType.Spell: parts.Add($"{card}:IsType(TYPE_SPELL)"); break;
            case FilterCardType.Trap: parts.Add($"{card}:IsType(TYPE_TRAP)"); break;
            case FilterCardType.SpellTrap: parts.Add($"{card}:IsType(TYPE_SPELL+TYPE_TRAP)"); break;
        }
        if (KindType(f.MonsterKind) is { } kind)
            parts.Add(kind.Not ? $"not {card}:{isType}({kind.Type})" : $"{card}:{isType}({kind.Type})");
        if (f.CardCode != 0)
            parts.Add($"{card}:{isCode}({string.Join(",", new[] { f.CardCode }.Concat(f.AltCodes).Select(Lua.Num))})");
        if (f.MentionsCode != 0)
            parts.Add(f.MentionsOrIs ? $"aux.IsCodeOrListed({card},{Lua.Num(f.MentionsCode)})" : $"aux.IsCodeListed({card},{Lua.Num(f.MentionsCode)})");
        // "a Normal or Quick-Play Spell": either kind.
        var kinds = new[] { f.SpellTrapKind, f.SpellTrapKind2 }.Where(k => k != SpellTrapKind.Any).Distinct().Select(k => KindTest(k, card)).ToList();
        if (kinds.Count == 1) parts.Add(kinds[0]);
        else if (kinds.Count > 1) parts.Add("(" + string.Join(" or ", kinds.Select(k => $"({k})")) + ")");
        switch (f.Position)
        {
            case FilterPosition.Attack: parts.Add($"{card}:IsAttackPos()"); break;
            case FilterPosition.Defense: parts.Add($"{card}:IsDefensePos()"); break;
            case FilterPosition.FaceUpAttack: parts.Add($"{card}:IsPosition(POS_FACEUP_ATTACK)"); break;
            case FilterPosition.FaceUpDefense: parts.Add($"{card}:IsPosition(POS_FACEUP_DEFENSE)"); break;
            case FilterPosition.FaceDownDefense: parts.Add($"{card}:IsPosition(POS_FACEDOWN_DEFENSE)"); break;
        }
        switch (f.Summoned)
        {
            case FilterSummoned.Special: parts.Add($"{card}:IsSummonType(SUMMON_TYPE_SPECIAL)"); break;
            case FilterSummoned.FromExtra: parts.Add($"{card}:IsSummonLocation(LOCATION_EXTRA)"); break;
            case FilterSummoned.Tribute: parts.Add($"{card}:IsSummonType(SUMMON_TYPE_ADVANCE)"); break;
            case FilterSummoned.ThisTurn: parts.Add($"{card}:IsStatus(STATUS_SUMMON_TURN+STATUS_FLIP_SUMMON_TURN+STATUS_SPSUMMON_TURN)"); break;
        }
        if (f.NotRevealed) parts.Add($"not {card}:IsPublic()");
        if (f.DefMin is { } dmin) parts.Add($"{card}:IsDefenseAbove({dmin})");
        if (f.DefMax is { } dmax) parts.Add($"{card}:IsDefenseBelow({dmax})");
        if (f.ExceptOwnName) parts.Add($"not {card}:{isCode}(id)");
        if (f.ExceptCode != 0) parts.Add($"not {card}:{isCode}({Lua.Num(f.ExceptCode)})");
        if (f.Archetype != 0 && f.Archetype2 != 0) parts.Add($"({card}:{isSet}({Lua.Hex(f.Archetype)}) or {card}:{isSet}({Lua.Hex(f.Archetype2)}))");
        else if (f.Archetype != 0) parts.Add($"{card}:{isSet}({Lua.Hex(f.Archetype)})");
        else if (f.Archetype2 != 0) parts.Add($"{card}:{isSet}({Lua.Hex(f.Archetype2)})");
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
        if (f.CenterZone) parts.Add($"{card}:IsLocation(LOCATION_MZONE) and {card}:GetSequence()==2");
        if (f.InThisColumn) parts.Add($"e:GetHandler():GetColumnGroup():IsContains({card})");
        // The filter's further requirements, from the property catalog: "original ATK 2000 or more", "not DARK".
        // A material's Type, Attribute, archetype or name in its material form, as the filter's own fields check them.
        string Material(string test) => material == SummonProcedureKind.None ? test : System.Text.RegularExpressions.Regex.Replace(test, @"\bc:Is(Type|Attribute|Race|SetCard|Code)\(",
            m => "c:" + m.Groups[1].Value switch { "Type" => isType, "Attribute" => isAttr, "Race" => isRace, "SetCard" => isSet, _ => isCode } + "(");
        string? Test(CardRequirement r) => CardProperties.Lua(r, hasEffect) is { } t
            ? Material(card == "c" ? t : System.Text.RegularExpressions.Regex.Replace(t, @"\bc:", card + ":"))
            : null;
        if (f.AnyRequirement)
        {
            var tests = f.Requirements.Select(Test).OfType<string>().ToList();
            if (tests.Count > 1) parts.Add("(" + string.Join(" or ", tests) + ")");
            else parts.AddRange(tests);
        }
        else
        {
            // Requirements in the same "or" group: any one of them; every group and every other requirement: all.
            foreach (var group in f.Requirements.GroupBy(r => r.Group > 0 ? r.Group : -1 - f.Requirements.IndexOf(r)))
            {
                var tests = group.Select(Test).OfType<string>().ToList();
                if (tests.Count > 1) parts.Add("(" + string.Join(" or ", tests) + ")");
                else parts.AddRange(tests);
            }
        }
        // "..., except "Exodia" Monster Cards whose original Levels are 10 or higher, and "Millennium" Monster Cards".
        foreach (var x in f.Except ?? [])
            if (Conditions(x, card, material, hasEffect) is { Count: > 0 } left) parts.Add($"not ({string.Join(" and ", left)})");
        if (f.FaceDownOnly) parts.Add($"{card}:IsFacedown()");
        else if (f.FaceUpOnly)
        {
            // Face-up counts on the field, among banished cards and in the Extra Deck ("face-up Pendulum Monsters in
            // your Extra Deck"); cards in the hand, Deck and GY are never face-down that way.
            var onlyPublicZones = (f.Zones & ~(Zone.Field | Zone.Banished | Zone.PendulumZone | Zone.ExtraDeck)) == Zone.None;
            parts.Add(onlyPublicZones
                ? $"{card}:IsFaceup()"
                : $"({card}:IsFaceup() or not {card}:IsLocation(LOCATION_ONFIELD+LOCATION_REMOVED{(f.Zones.HasFlag(Zone.ExtraDeck) ? "+LOCATION_EXTRA" : "")}))");
        }
        return parts;
    }

    /// <summary>The Lua test of a kind of Spell or Trap: "Continuous", "Quick-Play"; a Normal Spell/Trap has none of the others.</summary>
    private static string KindTest(SpellTrapKind kind, string card) => kind switch
    {
        SpellTrapKind.Normal => $"{card}:GetType()&(TYPE_CONTINUOUS+TYPE_QUICKPLAY+TYPE_FIELD+TYPE_EQUIP+TYPE_RITUAL+TYPE_COUNTER+TYPE_MONSTER)==0",
        SpellTrapKind.Continuous => $"{card}:IsType(TYPE_CONTINUOUS)",
        SpellTrapKind.QuickPlay => $"{card}:IsType(TYPE_QUICKPLAY)",
        SpellTrapKind.Field => $"{card}:IsType(TYPE_FIELD)",
        SpellTrapKind.Equip => $"{card}:IsType(TYPE_EQUIP)",
        SpellTrapKind.Ritual => $"{card}:IsType(TYPE_RITUAL) and {card}:IsType(TYPE_SPELL)",
        _ => $"{card}:IsType(TYPE_COUNTER)",
    };

    /// <summary>
    /// The other kind of card of "X or Y" as a filter of its own: what <paramref name="alt"/> says the card is, and what
    /// <paramref name="f"/> says about where it is, whether it is face-up, "other" and "except".
    /// </summary>
    public static CardFilter Alternative(CardFilter f, CardFilter alt)
    {
        var two = alt.Clone();
        two.OrCards = null;
        two.Owner = f.Owner;
        two.Zones = f.Zones;
        two.FaceUpOnly = f.FaceUpOnly;
        two.FaceDownOnly = f.FaceDownOnly;
        two.NotRevealed = f.NotRevealed;
        two.ExcludeThisCard = f.ExcludeThisCard;
        two.ExceptOwnName = f.ExceptOwnName;
        two.ExceptCode = f.ExceptCode;
        two.PointedToByThisCard = f.PointedToByThisCard;
        two.CenterZone = f.CenterZone;
        two.InThisColumn = f.InThisColumn;
        two.ThisCard = false;
        two.That = CardRef.None;
        return two;
    }
}
