using System.Globalization;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Compiler;

/// <summary>What kind of value a property has, which decides its operators and how it is entered.</summary>
public enum PropertyKind
{
    /// <summary>A number: ATK, Level, materials attached...</summary>
    Number,
    /// <summary>Bits, any of which may be picked: Attribute, Type, monster card type, position, where it was Summoned from.</summary>
    Flags,
    /// <summary>Separate choices: how it was Summoned, who owns it.</summary>
    Options,
    /// <summary>Archetype codes.</summary>
    Archetype,
    /// <summary>Card names (passcodes).</summary>
    CardName,
    /// <summary>Yes or no: its effects are negated, it was Summoned this turn...</summary>
    State,
}

/// <param name="Lua">Numbers: the getter on <c>c</c>. Flags: the test with {0} for the mask. Options: one test per choice. States: the test.</param>
/// <param name="Guard">A test the card must pass for the property to apply at all (Level needs a monster that has one).</param>
/// <param name="Noun">How the card text names it: "ATK", "original ATK", "Level", "materials attached".</param>
/// <param name="Choices">For flags and options: the values the picker offers.</param>
/// <param name="NeedsEffect">Uses the effect or its player (e, tp), which material filters don't have.</param>
public sealed record CardProperty(
    string Key, string Label, PropertyKind Kind, string Lua, string Noun,
    string? Guard = null, IReadOnlyList<(long Value, string Label, string Lua)>? Choices = null,
    string? IsText = null, string? IsNotText = null, bool NeedsEffect = false)
{
    public IReadOnlyList<RequirementOp> Ops => Kind switch
    {
        PropertyKind.Number =>
        [
            RequirementOp.GreaterOrEqual, RequirementOp.LessOrEqual, RequirementOp.Equal, RequirementOp.NotEqual,
            RequirementOp.Greater, RequirementOp.Less, RequirementOp.Between, RequirementOp.Outside,
        ],
        _ => [RequirementOp.Is, RequirementOp.IsNot],
    };
}

/// <summary>
/// Every card property a card filter's requirements can test, with its Lua and its card-text wording, in one place:
/// adding a property is one entry here. Only properties the engine (ygopro's Card API) reports reliably are listed.
/// </summary>
public static class CardProperties
{
    private static (long, string, string)[] Attributes() =>
        Enum.GetValues<CardAttribute>().Where(a => a != CardAttribute.None).Select(a => ((long)a, a.ToString().ToUpperInvariant(), "")).ToArray();

    private static (long, string, string)[] Races() =>
        Enum.GetValues<CardRace>().Where(r => r != CardRace.None).Select(r => ((long)r, EnumNames.Display(r), "")).ToArray();

    private static readonly (long, string, string)[] MonsterTypes =
    [
        (0x10, "Normal Monster", ""), (0x20, "Effect Monster", ""), (0x1000, "Tuner", ""), (0x40, "Fusion Monster", ""), (0x2000, "Synchro Monster", ""),
        (0x800000, "Xyz Monster", ""), (0x4000000, "Link Monster", ""), (0x80, "Ritual Monster", ""), (0x1000000, "Pendulum Monster", ""),
        (0x200000, "Flip monster", ""), (0x400000, "Toon monster", ""), (0x200, "Spirit monster", ""), (0x400, "Union monster", ""),
        (0x800, "Gemini monster", ""), (0x4000, "Token", ""),
    ];

    public static readonly IReadOnlyList<CardProperty> All =
    [
        // ---- numbers
        new("atk", "ATK", PropertyKind.Number, "GetAttack()", "ATK", Guard: "c:IsType(TYPE_MONSTER)"),
        new("def", "DEF", PropertyKind.Number, "GetDefense()", "DEF", Guard: "c:IsType(TYPE_MONSTER) and not c:IsType(TYPE_LINK)"),
        new("base_atk", "Original ATK", PropertyKind.Number, "GetBaseAttack()", "original ATK", Guard: "c:IsType(TYPE_MONSTER)"),
        new("base_def", "Original DEF", PropertyKind.Number, "GetBaseDefense()", "original DEF", Guard: "c:IsType(TYPE_MONSTER) and not c:IsType(TYPE_LINK)"),
        new("level", "Level", PropertyKind.Number, "GetLevel()", "Level", Guard: "c:GetLevel()>0"),
        new("orig_level", "Original Level", PropertyKind.Number, "GetOriginalLevel()", "original Level", Guard: "c:GetOriginalLevel()>0"),
        new("rank", "Rank", PropertyKind.Number, "GetRank()", "Rank", Guard: "c:IsType(TYPE_XYZ)"),
        new("link", "Link Rating", PropertyKind.Number, "GetLink()", "Link Rating", Guard: "c:IsType(TYPE_LINK)"),
        new("scale", "Pendulum Scale", PropertyKind.Number, "GetLeftScale()", "Pendulum Scale", Guard: "c:IsType(TYPE_PENDULUM)"),
        new("materials", "Materials attached (Xyz)", PropertyKind.Number, "GetOverlayCount()", "materials attached"),
        new("counters", "Counters on it", PropertyKind.Number, "GetCounter(0)", "counters on it"),
        new("equips", "Equip Cards equipped to it", PropertyKind.Number, "GetEquipCount()", "Equip Cards equipped to it"),
        new("linked", "Monsters it points to (Link)", PropertyKind.Number, "GetLinkedGroupCount()", "monsters it points to"),
        new("attacks", "Attacks it declared this turn", PropertyKind.Number, "GetAttackAnnouncedCount()", "attacks it declared this turn"),
        // ---- choices
        new("attribute", "Attribute", PropertyKind.Flags, "c:IsAttribute({0})", "Attribute", Choices: Attributes(), IsText: "that is {0}", IsNotText: "that is not {0}"),
        new("orig_attribute", "Original Attribute", PropertyKind.Flags, "c:GetOriginalAttribute()&{0}~=0", "original Attribute", Choices: Attributes(),
            IsText: "whose original Attribute is {0}", IsNotText: "whose original Attribute is not {0}"),
        new("race", "Monster Type", PropertyKind.Flags, "c:IsRace({0})", "Type", Choices: Races(), IsText: "that is a {0} monster", IsNotText: "that is not a {0} monster"),
        new("orig_race", "Original Type", PropertyKind.Flags, "c:GetOriginalRace()&{0}~=0", "original Type", Choices: Races(),
            IsText: "whose original Type is {0}", IsNotText: "whose original Type is not {0}"),
        new("monster_type", "Monster card type", PropertyKind.Flags, "c:IsType({0})", "card type", Choices: MonsterTypes, IsText: "that is a {0}", IsNotText: "that is not a {0}"),
        new("card_type", "Monster / Spell / Trap", PropertyKind.Flags, "c:IsType({0})", "card type",
            Choices: [(0x1, "monster", ""), (0x2, "Spell", ""), (0x4, "Trap", "")], IsText: "that is a {0}", IsNotText: "that is not a {0}"),
        new("position", "Battle position", PropertyKind.Flags, "c:IsPosition({0})", "position",
            Choices: [(0x3, "Attack Position", ""), (0xc, "Defense Position", ""), (0x5, "face-up", ""), (0xa, "face-down", "")],
            IsText: "in {0}", IsNotText: "not in {0}"),
        new("summoned_from", "Summoned from", PropertyKind.Flags, "c:IsSummonLocation({0})", "where it was Summoned from",
            Choices: [(0x2, "the hand", ""), (0x1, "the Deck", ""), (0x10, "the GY", ""), (0x20, "banishment", ""), (0x40, "the Extra Deck", "")],
            IsText: "that was Summoned from {0}", IsNotText: "that was not Summoned from {0}"),
        new("summon", "How it was Summoned", PropertyKind.Options, "", "how it was Summoned",
            Choices:
            [
                (0, "Normal", "c:IsSummonType(SUMMON_TYPE_NORMAL)"), (1, "Tribute", "c:IsSummonType(SUMMON_TYPE_ADVANCE)"),
                (2, "Flip", "c:IsSummonType(SUMMON_TYPE_FLIP)"), (3, "Special", "c:IsSummonType(SUMMON_TYPE_SPECIAL)"),
                (4, "Fusion", "c:IsSummonType(SUMMON_TYPE_FUSION)"), (5, "Synchro", "c:IsSummonType(SUMMON_TYPE_SYNCHRO)"),
                (6, "Xyz", "c:IsSummonType(SUMMON_TYPE_XYZ)"), (7, "Link", "c:IsSummonType(SUMMON_TYPE_LINK)"),
                (8, "Ritual", "c:IsSummonType(SUMMON_TYPE_RITUAL)"), (9, "Pendulum", "c:IsSummonType(SUMMON_TYPE_PENDULUM)"),
            ],
            IsText: "that was {0} Summoned", IsNotText: "that was not {0} Summoned"),
        new("owner", "Who owns it", PropertyKind.Options, "", "owner",
            Choices: [(0, "you", "c:GetOwner()==tp"), (1, "your opponent", "c:GetOwner()==1-tp")],
            IsText: "that {0} own(s)", IsNotText: "that {0} do(es) not own", NeedsEffect: true),
        new("archetype", "Archetype", PropertyKind.Archetype, "", "archetype", IsText: "that is a {0} card", IsNotText: "that is not a {0} card"),
        new("name", "Card name", PropertyKind.CardName, "", "name", IsText: "that is {0}", IsNotText: "that is not {0}"),
        // ---- states
        new("negated", "Its effects are negated", PropertyKind.State, "c:IsDisabled()", "", IsText: "whose effects are negated", IsNotText: "whose effects are not negated"),
        new("summoned_turn", "Summoned this turn", PropertyKind.State, "c:IsStatus(STATUS_SUMMON_TURN+STATUS_FLIP_SUMMON_TURN+STATUS_SPSUMMON_TURN)", "",
            IsText: "that was Summoned this turn", IsNotText: "that was not Summoned this turn"),
        new("attacked_turn", "Declared an attack this turn", PropertyKind.State, "c:GetAttackAnnouncedCount()>0", "",
            IsText: "that declared an attack this turn", IsNotText: "that has not declared an attack this turn"),
        new("battled_turn", "Battled this turn", PropertyKind.State, "c:GetBattledGroupCount()>0", "",
            IsText: "that battled this turn", IsNotText: "that has not battled this turn"),
        new("changed_atk", "ATK differs from its original ATK", PropertyKind.State, "c:IsType(TYPE_MONSTER) and c:GetAttack()~=c:GetBaseAttack()", "",
            IsText: "whose ATK is different from its original ATK", IsNotText: "whose ATK is its original ATK"),
        new("linked", "Is linked (Link Arrows)", PropertyKind.State, "c:IsLinkState()", "", IsText: "that is linked", IsNotText: "that is not linked"),
        new("equipped", "Has an Equip Card", PropertyKind.State, "c:GetEquipCount()>0", "", IsText: "that is equipped with an Equip Card", IsNotText: "that is not equipped with an Equip Card"),
        new("has_materials", "Has materials attached (Xyz)", PropertyKind.State, "c:GetOverlayCount()>0", "", IsText: "that has material", IsNotText: "that has no material"),
        new("extra_zone", "In an Extra Monster Zone", PropertyKind.State, "c:IsLocation(LOCATION_MZONE) and c:GetSequence()>4", "",
            IsText: "in the Extra Monster Zone", IsNotText: "in a Main Monster Zone"),
        new("targetable", "Can be targeted by this effect", PropertyKind.State, "c:IsCanBeEffectTarget(e)", "",
            IsText: "that can be targeted", IsNotText: "that cannot be targeted", NeedsEffect: true),
    ];

    public static CardProperty? Find(string key) => All.FirstOrDefault(p => p.Key == key);

    public static string OpLabel(RequirementOp op) => op switch
    {
        RequirementOp.Equal => "is exactly",
        RequirementOp.NotEqual => "is not",
        RequirementOp.Greater => "is more than",
        RequirementOp.GreaterOrEqual => "is at least",
        RequirementOp.Less => "is less than",
        RequirementOp.LessOrEqual => "is at most",
        RequirementOp.Between => "is between",
        RequirementOp.Outside => "is outside",
        RequirementOp.Is => "is",
        _ => "is not",
    };

    /// <summary>
    /// The Lua test of a requirement on card <c>c</c>, or null when it can't be written here (an unknown property, or
    /// one that needs the effect in a material filter).
    /// </summary>
    public static string? Lua(CardRequirement r, bool hasEffect = true)
    {
        if (Find(r.Property) is not { } p) return null;
        if (p.NeedsEffect && !hasEffect) return null;
        string test;
        switch (p.Kind)
        {
            case PropertyKind.Number:
            {
                var v = $"c:{p.Lua}";
                string N(int n) => n.ToString(CultureInfo.InvariantCulture);
                var (lo, hi) = (Math.Min(r.Value, r.Value2), Math.Max(r.Value, r.Value2));
                test = r.Op switch
                {
                    RequirementOp.Equal => $"{v}=={N(r.Value)}",
                    RequirementOp.NotEqual => $"{v}~={N(r.Value)}",
                    RequirementOp.Greater => $"{v}>{N(r.Value)}",
                    RequirementOp.Less => $"{v}<{N(r.Value)}",
                    RequirementOp.LessOrEqual => $"{v}<={N(r.Value)}",
                    RequirementOp.Between => $"{v}>={N(lo)} and {v}<={N(hi)}",
                    RequirementOp.Outside => $"({v}<{N(lo)} or {v}>{N(hi)})",
                    _ => $"{v}>={N(r.Value)}",
                };
                return p.Guard is null ? $"({test})" : $"({p.Guard} and {test})";
            }
            case PropertyKind.Flags:
            {
                var mask = r.Values.Aggregate(0L, (m, x) => m | x);
                if (mask == 0) return null;
                test = string.Format(CultureInfo.InvariantCulture, p.Lua, "0x" + mask.ToString("x", CultureInfo.InvariantCulture));
                break;
            }
            case PropertyKind.Options:
            {
                var tests = p.Choices!.Where(c => r.Values.Contains(c.Value)).Select(c => c.Lua).ToList();
                if (tests.Count == 0) return null;
                test = string.Join(" or ", tests);
                break;
            }
            case PropertyKind.Archetype:
            {
                var codes = r.Values.Where(v => v != 0).ToList();
                if (codes.Count == 0) return null;
                test = string.Join(" or ", codes.Select(code => $"c:IsSetCard(0x{code:x})"));
                break;
            }
            case PropertyKind.CardName:
            {
                var codes = r.Values.Where(v => v != 0).ToList();
                if (codes.Count == 0) return null;
                test = $"c:IsCode({string.Join(",", codes.Select(code => code.ToString(CultureInfo.InvariantCulture)))})";
                break;
            }
            default:
                test = p.Lua;
                break;
        }
        return r.Op == RequirementOp.IsNot ? $"not ({test})" : $"({test})";
    }

    /// <summary>"with 2000 or more original ATK", "that is not DARK", "whose effects are negated", or "" when incomplete.</summary>
    public static string Text(CardRequirement r, CardPack? pack)
    {
        if (Find(r.Property) is not { } p) return "";
        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        var (lo, hi) = (Math.Min(r.Value, r.Value2), Math.Max(r.Value, r.Value2));
        if (p.Kind == PropertyKind.Number)
        {
            var stat = p.Key is "atk" or "def" or "base_atk" or "base_def";
            var level = p.Key is "level" or "orig_level" or "rank" or "link" or "scale";
            // "with 2000 or more ATK", as cards say it; "whose Level is 4 or lower"; "with 2 or more materials attached".
            var amount = r.Op switch
            {
                RequirementOp.Equal => N(r.Value),
                RequirementOp.NotEqual => $"other than {N(r.Value)}",
                RequirementOp.Greater => $"more than {N(r.Value)}",
                RequirementOp.Less => $"less than {N(r.Value)}",
                RequirementOp.LessOrEqual => level ? $"{N(r.Value)} or lower" : $"{N(r.Value)} or less",
                RequirementOp.Between => $"{N(lo)} to {N(hi)}",
                RequirementOp.Outside => $"less than {N(lo)} or more than {N(hi)}",
                _ => level ? $"{N(r.Value)} or higher" : $"{N(r.Value)} or more",
            };
            if (stat) return r.Op is RequirementOp.NotEqual or RequirementOp.Outside ? $"whose {p.Noun} is {amount}" : $"with {amount} {p.Noun}";
            if (level) return $"whose {p.Noun} is {amount}";
            return r.Op is RequirementOp.NotEqual or RequirementOp.Outside ? $"whose number of {p.Noun} is {amount}" : $"with {amount} {p.Noun}";
        }
        if (p.Kind == PropertyKind.State) return (r.Op == RequirementOp.IsNot ? p.IsNotText : p.IsText) ?? "";
        var names = p.Kind switch
        {
            PropertyKind.Archetype => r.Values.Where(v => v != 0).Select(v => $"\"{(pack is not null ? pack.ArchetypeName((int)v) : ArchetypeNames.Find((int)v) ?? $"0x{v:x}")}\"").ToList(),
            PropertyKind.CardName => r.Values.Where(v => v != 0).Select(v => CardNames.Find(v, pack) is { } n ? $"\"{n}\"" : $"passcode {v}").ToList(),
            _ => p.Choices!.Where(c => p.Kind == PropertyKind.Options ? r.Values.Contains(c.Value) : r.Values.Any(v => (v & c.Value) == c.Value && v != 0))
                .Select(c => p.Key is "race" or "orig_race" && pack?.TypeName((CardRace)c.Value) is { } custom ? custom : c.Label).ToList(),
        };
        // Custom Types picked that aren't standard ones.
        if (p.Key is "race" or "orig_race" && pack is not null)
            foreach (var v in r.Values.Where(v => (v & EnumNames.StandardRaceMask) == 0 && v != 0)) names.Add(pack.TypeName((CardRace)v));
        if (names.Count == 0) return "";
        var not = r.Op == RequirementOp.IsNot;
        var joined = names.Count == 1 ? names[0]
            : not ? string.Join(" nor ", names) : string.Join(" or ", names);
        if (not && names.Count > 1)
            return (p.IsNotText ?? "not {0}").Replace(" not ", " neither ", StringComparison.Ordinal).Replace("{0}", joined, StringComparison.Ordinal);
        var text = (not ? p.IsNotText : p.IsText) ?? "{0}";
        if (p.Key == "owner")
            text = text.Replace("own(s)", names.Count == 1 && names[0] == "your opponent" ? "owns" : "own", StringComparison.Ordinal)
                .Replace("do(es)", names.Count == 1 && names[0] == "your opponent" ? "does" : "do", StringComparison.Ordinal);
        return text.Replace("{0}", joined, StringComparison.Ordinal);
    }
}
