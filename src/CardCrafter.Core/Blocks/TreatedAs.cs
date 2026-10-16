using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "(This card is always treated as a "HERO" card.)", "This card's name is also treated as "Dark Magician"", "This card is
/// also treated as a DARK monster", "is treated as a Tuner": what the picker names is added to this card (or, for
/// Attribute and Type with "instead", replaces them).
/// </summary>
public sealed class TreatedAsPassive : PassiveBlock
{
    public const string BlockId = "passive.treated_as";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card is also treated as an archetype / name / Attribute / Type / Tuner";
    public override string Sentence => "This card is {how} treated as {as} {where}";
    public override string Help => "Pick in the card picker what it is treated as: an archetype, a card name, an Attribute, a Type, or a monster kind such as Tuner. Several at once work too.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How", new("also", "also"), new("instead", "", "instead (Attribute and Type replace its own)")),
        ParamSpec.Cards("as", "Treated as", () => new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Tuner }),
        ParamSpec.Choice("where", "Where", new("always", "", "always (everywhere)"), new("field", "while on the field"), new("field_gy", "while on the field or in the GY")),
    ];

    private static string Where(BlockArgs args) => args.Choice("where") switch { "field" => " while it is on the field", "field_gy" => " while it is on the field or in the GY", _ => "" };

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("as");
        var also = args.Choice("how") == "also" ? "also " : "";
        var parts = new List<string>();
        string Arch(int code) => $"\"{(args.Pack is not null ? args.Pack.ArchetypeName(code) : ArchetypeNames.Find(code) ?? $"0x{code:x}")}\"";
        var archetypes = new[] { f.Archetype, f.Archetype2 }.Where(a => a != 0).Select(Arch).ToList();
        // "This card is always treated as a "HERO" card."
        if (archetypes.Count > 0) parts.Add($"this card is {(args.Choice("where") == "always" ? "always" : "also")} treated as {(archetypes.Count == 1 ? "a" : "an")} {string.Join(" and ", archetypes)} card".Replace("as an \"", "as a \"", StringComparison.Ordinal));
        if (f.CardCode != 0) parts.Add($"this card's name is also treated as {(CardNames.Find(f.CardCode, args.Pack) is { } n ? $"\"{n}\"" : $"passcode {f.CardCode}")}");
        var kind = new List<string>();
        if (f.Attribute != CardAttribute.None) kind.Add(EnumNames.Display(f.Attribute));
        if (f.Race != CardRace.None) kind.Add(args.Pack?.TypeName(f.Race) ?? EnumNames.Display(f.Race));
        if (kind.Count > 0 || f.MonsterKind != MonsterKind.Any)
        {
            var noun = f.MonsterKind switch
            {
                MonsterKind.Tuner => "Tuner", MonsterKind.Normal => "Normal Monster", MonsterKind.Effect => "Effect Monster",
                MonsterKind.Spirit => "Spirit monster", MonsterKind.Union => "Union monster", MonsterKind.Gemini => "Gemini monster",
                MonsterKind.Flip => "Flip monster", MonsterKind.Toon => "Toon monster", _ => "monster",
            };
            var words = string.Join(" ", kind.Append(noun));
            parts.Add($"this card is {also}treated as {("AEIOU".Contains(words[0]) ? "an" : "a")} {words}");
        }
        if (parts.Count == 0) return "";
        return string.Join(", and ", parts) + Where(args);
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Args.Cards("as");
        var instead = ctx.Args.Choice("how") == "instead";
        var field = ctx.Card.IsMonster ? "LOCATION_MZONE" : "LOCATION_SZONE";
        var range = ctx.Args.Choice("where") switch { "field" => field, "field_gy" => field + "+LOCATION_GRAVE", _ => null };
        void Add(string code, string value) => ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            range is null ? $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)" : $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE+EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)",
            .. range is null ? Array.Empty<string>() : [$"{v}:SetRange({range})"],
            $"{v}:SetCode({code})",
            $"{v}:SetValue({value})",
        ]);
        var any = false;
        foreach (var a in new[] { f.Archetype, f.Archetype2 }.Where(a => a != 0)) { Add("EFFECT_ADD_SETCODE", Lua.Hex(a)); any = true; }
        if (f.CardCode != 0) { Add("EFFECT_ADD_CODE", Lua.Num(f.CardCode)); any = true; }
        if (f.Attribute != CardAttribute.None) { Add(instead ? "EFFECT_CHANGE_ATTRIBUTE" : "EFFECT_ADD_ATTRIBUTE", EnumNames.LuaConstant(f.Attribute)); any = true; }
        if (f.Race != CardRace.None) { Add(instead ? "EFFECT_CHANGE_RACE" : "EFFECT_ADD_RACE", EnumNames.LuaConstant(f.Race)); any = true; }
        var type = f.MonsterKind switch
        {
            MonsterKind.Tuner => "TYPE_TUNER", MonsterKind.Normal => "TYPE_NORMAL", MonsterKind.Effect => "TYPE_EFFECT", MonsterKind.Spirit => "TYPE_SPIRIT",
            MonsterKind.Union => "TYPE_UNION", MonsterKind.Gemini => "TYPE_DUAL", MonsterKind.Flip => "TYPE_FLIP", MonsterKind.Toon => "TYPE_TOON", _ => null,
        };
        if (type is not null) { Add("EFFECT_ADD_TYPE", type); any = true; }
        if (!any) ctx.Effect.Error($"\"{Name}\": pick in the card picker what this card is treated as (an archetype, a name, an Attribute, a Type or a Tuner...).");
    }
}
