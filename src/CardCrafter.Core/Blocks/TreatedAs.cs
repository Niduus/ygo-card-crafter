using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "Special Summon 1 "Junk" Synchro Monster from your Extra Deck. (This is treated as a Synchro Summon.)": the "treated
/// as" option of the Special Summon blocks, written as official scripts write it. The monster gets no materials
/// (SetMaterial(nil)) and counts as properly Summoned afterwards (CompleteProcedure), so it can be revived later.
/// </summary>
public static class TreatedAsSummon
{
    /// <summary>The choice reads as nothing in the sentence: "(This is treated as ...)" follows it (the block's Afterword).</summary>
    public static ParamSpec Param => ParamSpec.Choice("as", "Treated as",
        new("none", "", "a plain Special Summon"),
        new("ritual", "", "treated as a Ritual Summon"),
        new("fusion", "", "treated as a Fusion Summon"),
        new("synchro", "", "treated as a Synchro Summon"),
        new("xyz", "", "treated as an Xyz Summon"),
        new("link", "", "treated as a Link Summon"));

    private static string Word(string kind) => kind switch { "ritual" => "Ritual", "fusion" => "Fusion", "synchro" => "Synchro", "xyz" => "Xyz", _ => "Link" };

    public static string SummonType(string kind) => "SUMMON_TYPE_" + kind.ToUpperInvariant();

    /// <summary>
    /// The second switch of IsCanBeSpecialSummoned/SpecialSummon: Ritual Monsters in the hand or Deck can only be
    /// Special Summoned with it ("must first be properly Summoned"), so official scripts lift it for Ritual Summons.
    /// </summary>
    public static string NoLimit(string kind, string ignore) => kind == "ritual" || ignore == "true" ? "true" : "false";

    /// <summary>The "must be used as material" effects (Destiny HERO - Dangerous...) the Summon must respect, or null.</summary>
    public static string? MustMaterial(string kind) => kind switch
    {
        "fusion" => "EFFECT_MUST_BE_FMATERIAL",
        "synchro" => "EFFECT_MUST_BE_SMATERIAL",
        "xyz" => "EFFECT_MUST_BE_XMATERIAL",
        "link" => "EFFECT_MUST_BE_LMATERIAL",
        _ => null,
    };

    /// <summary>"(This is treated as a Fusion Summon.)", or "" for a plain Special Summon.</summary>
    public static string Afterword(string kind) => kind == "none" ? "" : $"(This is treated as {(kind == "xyz" ? "an" : "a")} {Word(kind)} Summon.)";

    /// <summary>"Tribute this card; ...", "banish this card; ...": a cost that moves this card, freeing the zone it was in.</summary>
    public static bool CostFreesThisCard(BlockContext ctx) =>
        ctx.Effect.Source.Cost.Any(b => b.BlockId == "cost.this_card" || b.BlockId == "cost.tribute" && b.Filters.TryGetValue("cards", out var f) && f.ThisCard);

    private static string Leaving(BlockContext ctx) => CostFreesThisCard(ctx) ? "e:GetHandler()" : "nil";

    private static string MainRoom(BlockContext ctx) => CostFreesThisCard(ctx) ? "Duel.GetMZoneCount(tp,e:GetHandler())>0" : "Duel.GetLocationCount(tp,LOCATION_MZONE)>0";

    /// <summary>What each monster must pass: it can be Summoned that way and, from the Extra Deck, has a zone to go to.</summary>
    public static string AbleCheck(BlockContext ctx, string kind, string ignore, Zone zones)
    {
        var parts = new List<string> { $"c:IsCanBeSpecialSummoned(e,{SummonType(kind)},tp,{ignore},{NoLimit(kind, ignore)})" };
        // A Fusion Monster that has Fusion Materials to be Summoned with, as every official "treated as a Fusion Summon" checks.
        if (kind == "fusion") parts.Add("c:CheckFusionMaterial()");
        if (zones == Zone.ExtraDeck) parts.Add($"Duel.GetLocationCountFromEx(tp,tp,{Leaving(ctx)},c)>0");
        else if (zones.HasFlag(Zone.ExtraDeck))
            parts.Add($"(c:IsLocation(LOCATION_EXTRA) and Duel.GetLocationCountFromEx(tp,tp,{Leaving(ctx)},c)>0 or not c:IsLocation(LOCATION_EXTRA) and {MainRoom(ctx)})");
        return string.Join(" and ", parts);
    }

    /// <summary>The checks before anything is picked: the "must be used as material" effects, and room in the Main Monster Zones.</summary>
    public static string? SpaceCheck(BlockContext ctx, string kind, Zone zones)
    {
        var parts = new List<string>();
        if (MustMaterial(kind) is { } code) parts.Add($"aux.MustMaterialCheck(nil,tp,{code})");
        if (!zones.HasFlag(Zone.ExtraDeck)) parts.Add(MainRoom(ctx));
        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    /// <summary>The Summon of the one picked monster, returning how many were Summoned.</summary>
    public static string Apply(BlockContext ctx, string g, string kind, string ignore, string position, string? zone) =>
        ctx.Script.AddFunction("sptreated", "g,e,tp",
        [
            "local tc=g:GetFirst()",
            "tc:SetMaterial(nil)",
            $"if Duel.SpecialSummon(tc,{SummonType(kind)},tp,tp,{ignore},{NoLimit(kind, ignore)},{position}{(zone is null ? "" : "," + zone)})==0 then return 0 end",
            "tc:CompleteProcedure()",
            "return 1",
        ]) + $"({g},e,tp)";

    /// <summary>Official cards Summon exactly 1 face-up monster this way.</summary>
    public static bool Check(BlockContext ctx, string name, int count, bool faceDown)
    {
        var kind = Word(ctx.Args.Choice("as"));
        if (count != 1 || ctx.Args.CountMode == "all")
        {
            ctx.Effect.Error($"\"{name}\" treated as {(kind == "Xyz" ? "an" : "a")} {kind} Summon Summons 1 monster: set how many to 1.");
            return false;
        }
        if (faceDown)
        {
            ctx.Effect.Error($"\"{name}\": a Summon treated as {(kind == "Xyz" ? "an" : "a")} {kind} Summon is face-up. Pick another position.");
            return false;
        }
        return true;
    }
}

/// <summary>
/// "while in the hand or GY", "while on the field or in the GY": any mix of places, as official scripts list them
/// in SetRange (and aux.EnableChangeCode). The ticks show when the block's "where" is "in the places ticked below".
/// </summary>
public static class TreatedAsPlaces
{
    public const string Choice = "in";
    public static ChoiceOption Option => new(Choice, "", "in the places ticked below");

    private static bool Shown(BlockInstance b, string whereKey) => b.Values.GetValueOrDefault(whereKey) == Choice;

    public static IEnumerable<ParamSpec> Params(string whereKey = "where") =>
    [
        ParamSpec.Flag("in_hand", "in the hand", false).ShowIf(b => Shown(b, whereKey)),
        ParamSpec.Flag("in_deck", "in the Deck", false).ShowIf(b => Shown(b, whereKey)),
        ParamSpec.Flag("in_extra", "in the Extra Deck", false).ShowIf(b => Shown(b, whereKey)),
        ParamSpec.Choice("on_field", "On the field",
            new("none", "", "not on the field"),
            new("field", "on the field", "on the field (its own zone: Monster Zone for a monster, Spell & Trap Zone otherwise)"),
            new("mzone", "in the Monster Zone"),
            new("szone", "in the Spell & Trap Zone"),
            new("onfield", "on the field", "anywhere on the field (Monster and Spell & Trap Zones)")).ShowIf(b => Shown(b, whereKey)),
        // Ticked to begin with, so the choice reads as something: "while in the GY".
        ParamSpec.Flag("in_gy", "in the GY", true).ShowIf(b => Shown(b, whereKey)),
        ParamSpec.Flag("banished", "banished", false).ShowIf(b => Shown(b, whereKey)),
    ];

    /// <summary>"LOCATION_HAND+LOCATION_GRAVE", in the order official scripts write it; null when nothing is ticked.</summary>
    public static string? Range(BlockArgs args, CardData card)
    {
        var parts = new List<string>();
        if (args.Bool("in_hand")) parts.Add("LOCATION_HAND");
        if (args.Bool("in_deck")) parts.Add("LOCATION_DECK");
        if (args.Bool("in_extra")) parts.Add("LOCATION_EXTRA");
        switch (args.Choice("on_field"))
        {
            case "field": parts.Add(card.IsMonster ? "LOCATION_MZONE" : "LOCATION_SZONE"); break;
            case "mzone": parts.Add("LOCATION_MZONE"); break;
            case "szone": parts.Add("LOCATION_SZONE"); break;
            case "onfield": parts.Add("LOCATION_ONFIELD"); break;
        }
        if (args.Bool("in_gy")) parts.Add("LOCATION_GRAVE");
        if (args.Bool("banished")) parts.Add("LOCATION_REMOVED");
        return parts.Count == 0 ? null : string.Join("+", parts);
    }

    /// <summary>"in the hand or GY", "on the field or in the GY", "in the hand, on the field, or in the GY".</summary>
    public static string Text(BlockArgs args)
    {
        var places = new List<string>();
        if (args.Bool("in_hand")) places.Add("in the hand");
        if (args.Bool("in_deck")) places.Add("in the Deck");
        if (args.Bool("in_extra")) places.Add("in the Extra Deck");
        if (args.Choice("on_field") != "none") places.Add(args.Describe("on_field"));
        if (args.Bool("in_gy")) places.Add("in the GY");
        if (args.Bool("banished")) places.Add("banished");
        // "in the hand or GY": one "in the" for places that follow each other.
        for (var i = places.Count - 1; i > 0; i--)
            if (places[i].StartsWith("in the ", StringComparison.Ordinal) && places[i - 1].StartsWith("in the ", StringComparison.Ordinal))
                places[i] = places[i]["in the ".Length..];
        return places.Count switch
        {
            0 => "",
            1 => places[0],
            2 => $"{places[0]} or {places[1]}",
            _ => string.Join(", ", places.Take(places.Count - 1)) + ", or " + places[^1],
        };
    }
}

/// <summary>
/// "(This card is always treated as a "HERO" card.)", "This card's name is also treated as "Dark Magician"", "This card is
/// also treated as a DARK monster", "is treated as a Tuner", "This card is treated as a Normal Monster while in the hand
/// or GY": what the picker names is added to this card (or, for Attribute and Type with "instead", replaces them; a
/// Normal Monster "instead" is no longer an Effect Monster).
/// </summary>
public sealed class TreatedAsPassive : PassiveBlock
{
    public const string BlockId = "passive.treated_as";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card is also treated as an archetype / name / Attribute / Type / Tuner";
    public override string Sentence => "This card is {how} treated as {as} {where} {on_field}";
    public override string Help => "Pick in the card picker what it is treated as: an archetype, a card name, an Attribute, a Type, or a monster kind such as Tuner. " +
                                   "Several at once work too. \"Always\" archetypes go in the card's database entry, as official cards have them.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How", new("also", "also"), new("instead", "", "instead (Attribute and Type replace its own; a Normal Monster is no longer an Effect Monster)")),
        ParamSpec.Cards("as", "Treated as", () => new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Tuner }),
        ParamSpec.Choice("where", "Where", new("always", "", "always (everywhere)"), new("field", "while on the field"), new("field_gy", "while on the field or in the GY"),
            new("face_up", "while face-up on the field", "while face-up on the field (lost while its effects are negated)"),
            TreatedAsPlaces.Option),
        .. TreatedAsPlaces.Params(),
    ];

    private static string Where(BlockArgs args) => args.Choice("where") switch
    {
        "field" => " while it is on the field",
        "field_gy" => " while it is on the field or in the GY",
        TreatedAsPlaces.Choice when TreatedAsPlaces.Text(args) is { Length: > 0 } places => " while " + places,
        _ => "",
    };

    /// <summary>
    /// "Always" archetypes of an effect without IF blocks are the card's own: the database's setcode, as official cards
    /// keep "(This card is always treated as a "HERO" card.)". Elsewhere (or only under a condition) they are an effect.
    /// </summary>
    private static bool InDatabase(EffectEvent effect, BlockInstance block) =>
        effect.If.Count == 0 && new BlockArgs(BlockRegistry.Find(BlockId)!, block).Choice("where") == "always";

    private static IEnumerable<int> Archetypes(CardFilter f) => new[] { f.Archetype, f.Archetype2 }.Where(a => a != 0);

    /// <summary>The card's archetypes as its database entry stores them: the Card tab's, then the "always treated as" ones.</summary>
    public static List<int> Setcodes(CardProject project)
    {
        var codes = project.Card.Archetypes.Where(a => a != 0).ToList();
        if (project.UseCustomLua) return codes;
        foreach (var effect in project.Effects)
            foreach (var b in effect.Do.Where(b => b.BlockId == BlockId && InDatabase(effect, b)))
                foreach (var a in Archetypes(b.Filters.TryGetValue("as", out var f) ? f : new CardFilter()))
                    if (!codes.Contains(a)) codes.Add(a);
        return codes;
    }

    /// <summary>Writes the "always treated as" archetypes into the card's database row.</summary>
    public static void WriteSetcodes(CardProject project, CdbRow row)
    {
        var codes = Setcodes(project);
        if (codes.Count <= 4 && codes.Count > project.Card.Archetypes.Count(a => a != 0)) row.Setcode = CdbEncoder.PackSetcodes(codes);
    }

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("as");
        var also = args.Choice("how") == "also" ? "also " : "";
        var parts = new List<string>();
        string Arch(int code) => $"\"{(args.Pack is not null ? args.Pack.ArchetypeName(code) : ArchetypeNames.Find(code) ?? $"0x{code:x}")}\"";
        var archetypes = Archetypes(f).Select(Arch).ToList();
        var names = archetypes.Count <= 2 ? string.Join(" and ", archetypes) : string.Join(", ", archetypes.Take(archetypes.Count - 1)) + ", and " + archetypes[^1];
        // "(This card is always treated as a "HERO" card.)": the reminder official cards print first.
        string? reminder = null;
        if (archetypes.Count > 0 && args.Choice("where") == "always") reminder = $"(This card is always treated as {SummonWording.Article(names)} {names} card.)";
        else if (archetypes.Count > 0) parts.Add($"this card is also treated as {SummonWording.Article(names)} {names} card");
        if (f.CardCode != 0) parts.Add($"this card's name is also treated as {(CardNames.Find(f.CardCode, args.Pack) is { } n ? $"\"{n}\"" : $"passcode {f.CardCode}")}");
        var kind = new List<string>();
        // Treated as all of them: "LIGHT, WATER, FIRE, and WIND", not the "or" of a filter.
        static string All(string names) => names.Replace(", or ", ", and ", StringComparison.Ordinal).Replace(" or ", " and ", StringComparison.Ordinal);
        if (f.Attribute != CardAttribute.None) kind.Add(All(EnumNames.Display(f.Attribute)));
        if (f.Race != CardRace.None) kind.Add(All(args.Pack?.TypeName(f.Race) ?? EnumNames.Display(f.Race)));
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
        if (parts.Count == 0) return reminder ?? "";
        string rest;
        // "While face-up on the field, this card is also DARK-Attribute."
        if (args.Choice("where") == "face_up")
        {
            var attributeOnly = f.Attribute != CardAttribute.None && f.Race == CardRace.None && f.MonsterKind == MonsterKind.Any && archetypes.Count == 0 && f.CardCode == 0;
            // Elemental Mistress Doriado: "The Attribute of this card is also treated as ... while it is face-up on the field."
            if (attributeOnly && System.Numerics.BitOperations.PopCount((uint)f.Attribute) > 1)
                rest = $"the Attribute of this card is {also}treated as {All(EnumNames.Display(f.Attribute))} while it is face-up on the field";
            else rest = "while face-up on the field, " + (attributeOnly ? $"this card is {also}{EnumNames.Display(f.Attribute)}-Attribute" : string.Join(", and ", parts));
        }
        else rest = string.Join(", and ", parts) + Where(args);
        return reminder is null ? rest : $"{reminder} {SentenceCase.Upper(rest)}";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Args.Cards("as");
        var instead = ctx.Args.Choice("how") == "instead";
        var where = ctx.Args.Choice("where");
        var field = ctx.Card.IsMonster ? "LOCATION_MZONE" : "LOCATION_SZONE";
        var range = where switch
        {
            "field" or "face_up" => field,
            "field_gy" => field + "+LOCATION_GRAVE",
            TreatedAsPlaces.Choice => TreatedAsPlaces.Range(ctx.Args, ctx.Card),
            _ => null,
        };
        if (where == TreatedAsPlaces.Choice && range is null)
        {
            ctx.Effect.Error($"\"{Name}\": tick at least one place where this card is treated as it.");
            return;
        }
        // "While face-up on the field" and the places ticked are ordinary effects of the card, as official scripts
        // write them (aux.EnableChangeCode too): lost while the card's effects are negated.
        var plain = where is "face_up" or TreatedAsPlaces.Choice;
        void Add(string code, string value) => ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            plain ? $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)"
            : range is null ? $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)" : $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE+EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)",
            .. range is null ? Array.Empty<string>() : [$"{v}:SetRange({range})"],
            $"{v}:SetCode({code})",
            $"{v}:SetValue({value})",
        ]);
        var any = false;
        if (InDatabase(ctx.Effect.Source, ctx.Args.Instance))
        {
            // The card's database entry carries them (PackExporter writes Setcodes).
            any = Archetypes(f).Any();
            if (any && Setcodes(ctx.Script.Project).Count > 4)
                ctx.Effect.Error("A card can have at most 4 archetypes, counting the Card tab's and the ones it is always treated as.");
        }
        else foreach (var a in Archetypes(f)) { Add("EFFECT_ADD_SETCODE", Lua.Hex(a)); any = true; }
        if (f.CardCode != 0) { Add("EFFECT_ADD_CODE", Lua.Num(f.CardCode)); any = true; }
        if (f.Attribute != CardAttribute.None) { Add(instead ? "EFFECT_CHANGE_ATTRIBUTE" : "EFFECT_ADD_ATTRIBUTE", EnumNames.LuaConstant(f.Attribute)); any = true; }
        if (f.Race != CardRace.None) { Add(instead ? "EFFECT_CHANGE_RACE" : "EFFECT_ADD_RACE", EnumNames.LuaConstant(f.Race)); any = true; }
        var type = f.MonsterKind switch
        {
            MonsterKind.Tuner => "TYPE_TUNER", MonsterKind.Normal => "TYPE_NORMAL", MonsterKind.Effect => "TYPE_EFFECT", MonsterKind.Spirit => "TYPE_SPIRIT",
            MonsterKind.Union => "TYPE_UNION", MonsterKind.Gemini => "TYPE_DUAL", MonsterKind.Flip => "TYPE_FLIP", MonsterKind.Toon => "TYPE_TOON", _ => null,
        };
        if (type is not null) { Add("EFFECT_ADD_TYPE", type); any = true; }
        // "This card is treated as a Normal Monster": it stops being an Effect Monster, as official scripts remove it.
        if (type == "TYPE_NORMAL" && instead) Add("EFFECT_REMOVE_TYPE", "TYPE_EFFECT");
        if (!any) ctx.Effect.Error($"\"{Name}\": pick in the card picker what this card is treated as (an archetype, a name, an Attribute, a Type or a Tuner...).");
    }
}

/// <summary>
/// How this card counts as material: "If this card is used for the Xyz Summon of a WATER monster, it can be treated as
/// a Level 3 or 5 monster", "This card on the field can be treated as a Level 8 monster when used for a Synchro
/// Summon", "you can treat it as a non-Tuner", "this card can be used as the entire Tribute", "This card can be used as
/// a substitute for any 1 Fusion Material...", "can be treated as 2 materials for the Xyz Summon of ...".
/// </summary>
public sealed class MaterialTreatedAsPassive : PassiveBlock
{
    public const string BlockId = "passive.material_treated_as";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card can be treated as a Level, non-Tuner, Fusion substitute... as material";
    public override string Sentence => "As material, this card can be treated as {kind} {level} {level2} for {for} {where} {name}";
    public override string Help => "Leave the monster being Summoned as plain monsters for any. Put it under \"While this card is face-up\".";

    private static bool IsKind(BlockInstance b, params string[] kinds) => kinds.Contains(b.Values.GetValueOrDefault("kind", "xyz_level"));

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("kind", "Treated as",
            new("xyz_level", "a Level", "a Level N monster for an Xyz Summon"),
            new("synchro_level", "a Level", "a Level N monster for a Synchro Summon"),
            new("nontuner", "a non-Tuner", "a non-Tuner for a Synchro Summon"),
            new("ritual_entire", "the entire Tribute", "the entire Tribute for a Ritual Summon"),
            new("fusion_substitute", "a substitute", "a substitute for any 1 Fusion Material whose name is listed"),
            new("xyz_double", "2 materials", "2 materials for an Xyz Summon that requires 3 or more"),
            // Crowley, the Gifted Magistus: "If this face-up card is used as Fusion Material, its name can be treated as "Aleister the Invoker"".
            new("fusion_code", "another name", "another card's name, as Fusion Material (picked below)")),
        ParamSpec.Int("level", "Level", 4, 1, 12).ShowIf(b => IsKind(b, "xyz_level", "synchro_level")),
        ParamSpec.Int("level2", "Or Level (0 for none)", 0, 0, 12).ShowIf(b => IsKind(b, "xyz_level")),
        new ParamSpec
        {
            Key = "for", Label = "For the Summon of", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster },
        },
        ParamSpec.Choice("where", "Where", new("field", "on the field", "while this card is on the field"), new("any", "", "wherever it is used from"))
            .ShowIf(b => IsKind(b, "synchro_level", "ritual_entire", "fusion_substitute", "fusion_code")),
        new ParamSpec
        {
            Key = "name", Label = "Its name can be treated as", Type = ParamType.Cards, WhereFromBlock = true,
            // "Aleister the Invoker" by default.
            DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster, CardCode = 86120751 },
        }.Unprinted().WhenUsed().ShowIf(b => IsKind(b, "fusion_code")),
    ];

    private static string KindOf(BlockArgs args) => args.Choice("kind");

    /// <summary>The monster being Summoned, without where it is: "a WATER monster", "a "Number" monster".</summary>
    private static CardFilter For(BlockArgs args, MonsterKind kind = MonsterKind.Any)
    {
        var f = args.Cards("for").Clone();
        f.Zones = Zone.None;
        f.Owner = CardOwner.Either;
        f.CardType = FilterCardType.Monster;
        if (kind != MonsterKind.Any) f.MonsterKind = kind;
        return f;
    }

    /// <summary>Whether the picker names any monster at all, so "of a WATER monster" is said.</summary>
    private static bool Picks(BlockArgs args) => FilterCompiler.Conditions(For(args), "c", hasEffect: false).Any(p => p != "c:IsType(TYPE_MONSTER)");

    private static string Noun(BlockArgs args, MonsterKind kind = MonsterKind.Any)
    {
        var noun = FilterText.Describe(For(args, kind), args.Pack, withLocation: false);
        return $"{SummonWording.Article(noun)} {noun}";
    }

    private static string Levels(BlockArgs args) =>
        args.Int("level2") > 0 && args.Int("level2") != args.Int("level") && KindOf(args) == "xyz_level" ? $"Level {args.Int("level")} or {args.Int("level2")}" : $"Level {args.Int("level")}";

    public override string Describe(BlockArgs args)
    {
        var field = args.Choice("where") == "field";
        return KindOf(args) switch
        {
            "synchro_level" => $"this card{(field ? " on the field" : "")} can be treated as a {Levels(args)} monster when used for {(Picks(args) ? "the Synchro Summon of " + Noun(args) : "a Synchro Summon")}",
            "nontuner" => $"if this card you control is used as Synchro Material{(Picks(args) ? " for " + Noun(args) : "")}, you can treat it as a non-Tuner",
            "ritual_entire" => $"if you Ritual Summon exactly 1 {FilterText.Describe(For(args, MonsterKind.Ritual), args.Pack, withLocation: false)} with a card effect that requires use of monsters, " +
                               $"this card{(field ? " you control" : "")} can be used as the entire Tribute",
            "fusion_substitute" => $"this card{(field ? " on the field" : "")} can be used as a substitute for any 1 Fusion Material whose name is specifically listed on " +
                                   $"{(Picks(args) ? Noun(args, MonsterKind.Fusion) : "the Fusion Monster Card")}, but the other Fusion Material(s) must be correct",
            "xyz_double" => $"this card can be treated as 2 materials for the Xyz Summon of {Noun(args, MonsterKind.Xyz)} that requires 3 or more materials",
            "fusion_code" => $"if this {(field ? "face-up " : "")}card is used as Fusion Material, its name can be treated as {NameText(args)}",
            _ => $"if this card is used for {(Picks(args) ? "the Xyz Summon of " + Noun(args) : "an Xyz Summon")}, it can be treated as a {Levels(args)} monster",
        };
    }

    /// <summary>""Aleister the Invoker"": the name the card can be treated as, or "another card's name" while none is picked.</summary>
    private static string NameText(BlockArgs args) =>
        args.Cards("name").CardCode is var code and not 0 ? CardNames.Find(code, args.Pack) is { } n ? $"\"{n}\"" : $"passcode {code}" : "another card's name";

    /// <summary>The checks on the monster being Summoned, for the value function's <paramref name="card"/>.</summary>
    private static List<string> Checks(BlockArgs args, string card, MonsterKind drop = MonsterKind.Any)
    {
        var f = For(args);
        if (f.MonsterKind == drop) f.MonsterKind = MonsterKind.Any;
        return FilterCompiler.Conditions(f, card, hasEffect: false).Where(p => p != $"{card}:IsType(TYPE_MONSTER)").ToList();
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var args = ctx.Args;
        var field = args.Choice("where") == "field";
        var n = Lua.Num(args.Int("level"));
        switch (KindOf(args))
        {
            case "xyz_level":
            {
                // The Level the Xyz Summon may use goes in the upper half of the value, one effect per Level (Lantern Shark).
                var checks = Checks(args, "rc");
                var value = ctx.Script.AddFunction("xyzlv", "e,c,rc", checks.Count == 0
                    ? ["return c:GetLevel()+0x10000*e:GetLabel()"]
                    : [$"if {string.Join(" and ", checks)} then", "\treturn c:GetLevel()+0x10000*e:GetLabel()", "else", "\treturn c:GetLevel()", "end"]);
                var levels = new List<int> { args.Int("level") };
                if (args.Int("level2") > 0 && args.Int("level2") != args.Int("level")) levels.Add(args.Int("level2"));
                foreach (var lv in levels)
                    ctx.Effect.AddPassive(v =>
                    [
                        $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                        $"{v}:SetCode(EFFECT_XYZ_LEVEL)",
                        $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)",
                        $"{v}:SetRange(LOCATION_MZONE)",
                        $"{v}:SetValue({value})",
                        $"{v}:SetLabel({Lua.Num(lv)})",
                    ]);
                break;
            }
            case "synchro_level":
            {
                var checks = Checks(args, "c");
                var value = ctx.Script.AddFunction("slevel", "e,c", checks.Count == 0
                    ? ["local lv=aux.GetCappedLevel(e:GetHandler())", $"return ({n}<<16)+lv"]
                    : ["local lv=aux.GetCappedLevel(e:GetHandler())", $"if {string.Join(" and ", checks)} then", $"\treturn ({n}<<16)+lv", "else", "\treturn lv", "end"]);
                ctx.Effect.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                    .. field ? new[] { $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)", $"{v}:SetRange(LOCATION_MZONE)" } : [],
                    $"{v}:SetCode(EFFECT_SYNCHRO_LEVEL)",
                    $"{v}:SetValue({value})",
                ]);
                break;
            }
            case "nontuner":
            {
                var value = ctx.Script.AddFunction("tnval", "e,c", [$"return {string.Join(" and ", Checks(args, "c").Prepend("e:GetHandler():IsControler(c:GetControler())"))}"]);
                ctx.Effect.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                    $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)",
                    $"{v}:SetRange(LOCATION_MZONE)",
                    $"{v}:SetCode(EFFECT_NONTUNER)",
                    $"{v}:SetValue({value})",
                ]);
                break;
            }
            case "ritual_entire":
            {
                // Its Level counts as the Ritual Monster's own Level (in the upper half), as the Gishki and Nekroz cards write it.
                var checks = Checks(args, "c", MonsterKind.Ritual);
                var body = new List<string>();
                if (field) body.AddRange(["local ec=e:GetHandler()", "local lv=aux.GetCappedLevel(ec)", "if not ec:IsLocation(LOCATION_MZONE) then return lv end"]);
                else body.Add("local lv=aux.GetCappedLevel(e:GetHandler())");
                if (checks.Count == 0) body.AddRange(["local clv=c:GetLevel()", "return (lv<<16)+clv"]);
                else body.AddRange([$"if {string.Join(" and ", checks)} then", "\tlocal clv=c:GetLevel()", "\treturn (lv<<16)+clv", "else return lv end"]);
                var value = ctx.Script.AddFunction("rlevel", "e,c", body);
                ctx.Effect.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                    $"{v}:SetCode(EFFECT_RITUAL_LEVEL)",
                    $"{v}:SetValue({value})",
                ]);
                break;
            }
            case "fusion_substitute":
            {
                var checks = Checks(args, "c", MonsterKind.Fusion);
                var value = checks.Count == 0 ? null : ctx.Script.AddFunction("subval", "e,c", [$"return {string.Join(" and ", checks)}"]);
                // In the hand, on the field or in the GY, where Fusion Materials come from (not the Deck), as official substitutes write it.
                if (!field && ctx.Effect.Source.If.Count > 0)
                    ctx.Effect.Error($"\"{Name}\": a Fusion substitute used from anywhere can't also have IF blocks; pick \"while this card is on the field\".");
                var con = field ? null : ctx.Script.AddFunction("subcon", "e", ["return e:GetHandler():IsLocation(LOCATION_HAND+LOCATION_MZONE+LOCATION_GRAVE)"]);
                ctx.Effect.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                    $"{v}:SetCode(EFFECT_FUSION_SUBSTITUTE)",
                    .. field ? new[] { $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)", $"{v}:SetRange(LOCATION_MZONE)" } : [],
                    .. con is null ? Array.Empty<string>() : [$"{v}:SetCondition({con})"],
                    .. value is null ? Array.Empty<string>() : [$"{v}:SetValue({value})"],
                ]);
                break;
            }
            case "fusion_code":
            {
                var code = args.Cards("name").CardCode;
                if (code == 0) { ctx.Effect.Error($"\"{Name}\": pick the card whose name it can be treated as."); return; }
                // As Crowley, the Gifted Magistus: an extra name its Fusion Material checks see, while it is face-up on the field.
                var con = field ? ctx.Script.AddFunction("fcodecon", "e", ["local c=e:GetHandler()", "return c:IsLocation(LOCATION_MZONE) and c:IsFaceup()"]) : null;
                ctx.Effect.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                    $"{v}:SetCode(EFFECT_ADD_FUSION_CODE)",
                    .. con is null ? Array.Empty<string>() : [$"{v}:SetCondition({con})"],
                    $"{v}:SetValue({Lua.Num(code)})",
                ]);
                break;
            }
            case "xyz_double":
            {
                // Xyz procedures count it twice for Xyz Monsters the target accepts; the value keeps it to one per Summon (Drake Shark).
                var checks = Checks(args, "c", MonsterKind.Xyz);
                var target = checks.Count == 0 ? null : ctx.Script.AddFunction("sxyzfilter", "e,c", [$"return {string.Join(" and ", checks)}"]);
                ctx.Effect.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                    $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)",
                    $"{v}:SetCode(EFFECT_DOUBLE_XMATERIAL)",
                    $"{v}:SetRange(LOCATION_MZONE)",
                    .. target is null ? Array.Empty<string>() : [$"{v}:SetTarget({target})"],
                    $"{v}:SetValue(id)",
                ], limited: true);
                break;
            }
        }
    }
}

/// <summary>
/// "All Fusion Monsters you control are also treated as "Frightfur" monsters.", "All EARTH monsters you control are also
/// treated as "Vernusylph" monsters.": other monsters gain an archetype, a name, an Attribute, a Type or a kind while this
/// card is face-up (passive.become replaces a Type or Attribute instead).
/// </summary>
public sealed class CardsTreatedAsPassive : PassiveBlock
{
    public const string BlockId = "passive.cards_treated_as";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Monsters are also treated as an archetype / name / Attribute / Type";
    public override string Sentence => "{cards} are also treated as {as}";
    public override string Help => "Like Frightfur Sanctuary. Pick in the second picker what they are also treated as: one archetype, a name, an Attribute, a Type or a kind such as Tuner.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
        new ParamSpec { Key = "as", Label = "Also treated as", Type = ParamType.Cards, WhereFromBlock = true, DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster, Archetype = 0x8 } },
    ];

    /// <summary>What they are treated as, without where: ""Frightfur" monsters", "DARK Dragon monsters", "Tuners".</summary>
    private static string What(BlockArgs args)
    {
        var f = args.Cards("as");
        var parts = new List<string>();
        if (f.CardCode != 0) parts.Add(CardNames.Find(f.CardCode, args.Pack) is { } n ? $"\"{n}\"" : $"passcode {f.CardCode}");
        var kind = new CardFilter { CardType = FilterCardType.Monster, Archetype = f.Archetype, Attribute = f.Attribute, Race = f.Race, MonsterKind = f.MonsterKind };
        if (FilterCompiler.Conditions(kind).Count > 1) parts.Add(FilterText.Describe(kind, args.Pack, plural: true, withLocation: false));
        return string.Join(" and ", parts);
    }

    public override string Describe(BlockArgs args)
    {
        var noun = FilterText.Describe(args.Cards("cards"), args.Pack, plural: true, preposition: "", ownName: args.CardName, withExcept: false);
        return $"all {noun} are also treated as {What(args)}{args.ExceptText()}";
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Zones = Zone.MonsterZone;
        filter.CardType = FilterCardType.Monster;
        var f = ctx.Script.Filter(filter);
        var target = BecomePassive.NeedsTarget(filter)
            ? ctx.Script.AddFunction("treatedtg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")])
            : null;
        var range = RangeOf(ctx);
        void Add(string code, string value) => ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange({f.Self},{f.Opp})",
            .. target is null ? Array.Empty<string>() : new[] { $"{v}:SetTarget({target})" },
            $"{v}:SetCode({code})",
            $"{v}:SetValue({value})",
        ]);
        var a = ctx.Args.Cards("as");
        if (a.Archetype2 != 0) ctx.Effect.Error($"\"{Name}\": pick one archetype (use the block twice for two).");
        var any = false;
        if (a.Archetype != 0) { Add("EFFECT_ADD_SETCODE", Lua.Hex(a.Archetype)); any = true; }
        if (a.CardCode != 0) { Add("EFFECT_ADD_CODE", Lua.Num(a.CardCode)); any = true; }
        if (a.Attribute != CardAttribute.None) { Add("EFFECT_ADD_ATTRIBUTE", EnumNames.LuaConstant(a.Attribute)); any = true; }
        if (a.Race != CardRace.None) { Add("EFFECT_ADD_RACE", EnumNames.LuaConstant(a.Race)); any = true; }
        var type = a.MonsterKind switch
        {
            MonsterKind.Tuner => "TYPE_TUNER", MonsterKind.Normal => "TYPE_NORMAL", MonsterKind.Effect => "TYPE_EFFECT", MonsterKind.Spirit => "TYPE_SPIRIT",
            MonsterKind.Union => "TYPE_UNION", MonsterKind.Gemini => "TYPE_DUAL", MonsterKind.Flip => "TYPE_FLIP", MonsterKind.Toon => "TYPE_TOON", _ => null,
        };
        if (type is not null) { Add("EFFECT_ADD_TYPE", type); any = true; }
        if (!any) ctx.Effect.Error($"\"{Name}\": pick in the second picker what the monsters are also treated as.");
    }
}
