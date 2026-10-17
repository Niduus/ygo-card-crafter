using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Templates;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;

namespace CardCrafter.Core.Assistant;

/// <summary>One tool the assistant can call: its name, what it is for, and the JSON schema of its input.</summary>
public sealed record AssistantTool(string Name, string Description, string InputSchema, IReadOnlyList<string> Required);

/// <summary>What a tool call gave back: the text for the model, and a line for the chat when it changed something.</summary>
public sealed record ToolOutcome(string Content, bool IsError = false, string? Activity = null);

/// <summary>
/// The tools the assistant uses to read and change the user's cards. Every change goes through the same model the editor
/// uses (blocks, filters, card data), is checked against the block catalog, and comes back with the compiler's verdict, so
/// the assistant sees the card text and any problems right away, just as the user does on the Effects tab.
/// </summary>
public sealed class AssistantTools(IAssistantWorkspace workspace)
{
    private static readonly JsonSerializerOptions Json = PackSerializer.Options;
    private static readonly JsonSerializerOptions Compact = new(PackSerializer.Options) { WriteIndented = false };

    public static IReadOnlyList<AssistantTool> All { get; } =
    [
        new("find_blocks",
            "Search the block catalog by words in a block's id, name, sentence or help (e.g. \"special summon\", \"destroyed\", \"lp\"). "
            + "Returns matching blocks with their id, kind and sentence. Use describe_block on one to see its options.",
            """{"query":{"type":"string","description":"Words to look for. Empty lists every block of the kind."},"kind":{"type":"string","enum":["when","if","cost","do","passive"],"description":"Only blocks of this kind."}}""",
            []),
        new("describe_block",
            "Everything about one block: its sentence, help, and each option (key, type, default, allowed values, range). "
            + "Read this before placing a block, and use exactly these keys and values.",
            """{"block_id":{"type":"string","description":"The block's id, such as \"do.special_summon\"."}}""",
            ["block_id"]),
        new("describe_model",
            "The JSON fields of the editor's data types, with their types, allowed enum values and defaults: \"card\" (name, kind, stats, Types...), "
            + "\"filter\" (a card picker), \"requirement\" (with the catalog of card properties a filter can compare), \"effect\" (one row of WHEN/IF/COST/DO), "
            + "\"block\" (a placed block) or \"procedure\" (how an Extra Deck monster is Summoned).",
            """{"type":{"type":"string","enum":["card","filter","requirement","effect","block","procedure"]}}""",
            ["type"]),
        new("list_templates",
            "The ready-made effects (\"Draw 2\" like Pot of Greed, searchers, revivers...). get_template returns one as an effect you can add or change.",
            "{}", []),
        new("get_template",
            "One template as effect JSON, ready for add_effect.",
            """{"name":{"type":"string","description":"The template's name, as list_templates gives it."}}""",
            ["name"]),
        new("list_cards",
            "The cards in the open pack (index, passcode, name, kind) and which one is selected, plus the pack's archetypes and custom Types.",
            "{}", []),
        new("select_card",
            "Selects another card of the pack; the other tools then work on it.",
            """{"index":{"type":"integer","description":"0-based index from list_cards."}}""",
            ["index"]),
        new("new_card",
            "Adds a new card to the pack and selects it.",
            """{"kind":{"type":"string","enum":["monster","spell","trap"]}}""",
            ["kind"]),
        new("get_card",
            "The selected card as JSON (card data, effects as blocks, Summon procedure, equip targets, script settings) with its generated card text and the compiler's problems.",
            "{}", []),
        new("update_card",
            "Changes fields of the selected card. Give only the fields to change. \"card\" takes card fields (describe_model card), \"procedure\" the Extra Deck Summon procedure, "
            + "\"equipFilter\" which monsters an Equip Spell can equip to (a filter).",
            """{"card":{"type":"object","description":"Card fields to change, e.g. {\"name\":\"Ember Knight\",\"attribute\":\"fire\",\"atk\":1800}."},"procedure":{"type":"object","description":"Summon procedure fields to change."},"equipFilter":{"type":"object","description":"A filter replacing the equip targets."}}""",
            []),
        new("add_effect",
            "Adds an effect (a row of WHEN, IF, COST and DO blocks) to the selected card. Returns the card text and any problems.",
            """{"effect":{"type":"object","description":"The effect, as describe_model effect shows."},"position":{"type":"integer","description":"1-based place to insert it; the end when left out."}}""",
            ["effect"]),
        new("replace_effect",
            "Replaces one effect of the selected card with a new version.",
            """{"number":{"type":"integer","description":"The effect's 1-based number, as get_card lists it."},"effect":{"type":"object"}}""",
            ["number", "effect"]),
        new("remove_effect",
            "Removes one effect of the selected card.",
            """{"number":{"type":"integer","description":"The effect's 1-based number."}}""",
            ["number"]),
        new("organize_card",
            "Tidies the selected card so its text reads like an official card, without changing what it does: effects in printed order (Summon procedures, "
            + "a Spell/Trap's activation, lasting effects, hand effects, \"if this card is Summoned\", effects you activate, Quick Effects, other triggers, GY effects last), "
            + "conditions in reading order, costs on this card first, repeated conditions removed, and effects that only differ in what starts them merged with \"or when\". "
            + "DO blocks are never reordered. Each change is kept only if the compiled script does the same. Returns what changed.",
            "{}", []),
        new("compile_card",
            "Builds the selected card's Lua script, as it will be installed into YGO Omega, with every problem the compiler and the script checker find.",
            "{}", []),
        new("translate_script",
            "Reads a card script (Lua, e.g. an official card's) into blocks, the way the app's translator does. Each part says whether it translates exactly and why not. "
            + "Translated parts come back as effect JSON, which shows how the blocks express that wording.",
            """{"lua":{"type":"string","description":"The whole script."}}""",
            ["lua"]),
        new("set_script",
            "Gives the selected card a hand-written Lua script instead of its blocks (use_blocks:false), or switches it back to its blocks (use_blocks:true). "
            + "Only for what no block can express; the script is checked against the engine's API.",
            """{"lua":{"type":"string","description":"The whole script, when use_blocks is false."},"use_blocks":{"type":"boolean"}}""",
            ["use_blocks"]),
    ];

    /// <summary>Runs one tool. Errors come back as an error outcome the model can correct, never as an exception.</summary>
    public ToolOutcome Run(string name, JsonElement input)
    {
        try
        {
            return name switch
            {
                "find_blocks" => FindBlocks(Str(input, "query") ?? "", Str(input, "kind")),
                "describe_block" => DescribeBlock(Str(input, "block_id") ?? ""),
                "describe_model" => DescribeModel(Str(input, "type") ?? ""),
                "list_templates" => ListTemplates(),
                "get_template" => GetTemplate(Str(input, "name") ?? ""),
                "list_cards" => ListCards(),
                "select_card" => SelectCard(Int(input, "index") ?? -1),
                "new_card" => NewCard(Str(input, "kind") ?? "monster"),
                "get_card" => GetCard(),
                "update_card" => UpdateCard(input),
                "add_effect" => AddEffect(input),
                "replace_effect" => ReplaceEffect(input),
                "remove_effect" => RemoveEffect(Int(input, "number") ?? 0),
                "organize_card" => OrganizeCard(),
                "compile_card" => CompileCard(),
                "translate_script" => TranslateScript(Str(input, "lua") ?? ""),
                "set_script" => SetScript(input),
                _ => new ToolOutcome($"There is no tool named \"{name}\".", true),
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return new ToolOutcome($"That input couldn't be used: {ex.Message}", true);
        }
    }

    // ---------------------------------------------------------------- reference

    public static string KindName(BlockKind kind) => kind switch
    {
        BlockKind.Trigger => "when",
        BlockKind.Condition => "if",
        BlockKind.Cost => "cost",
        BlockKind.Action => "do",
        _ => "passive",
    };

    private static ToolOutcome FindBlocks(string query, string? kind)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = BlockRegistry.All.Where(b => !b.Hidden)
            .Where(b => kind is null || KindName(b.Kind) == kind)
            .Where(b => words.All(w => $"{b.Id} {b.Name} {b.Sentence} {b.Help} {b.Group}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(60)
            .Select(b => new { id = b.Id, kind = KindName(b.Kind), name = b.Name, sentence = b.Sentence })
            .ToList();
        return found.Count == 0
            ? new ToolOutcome("No block matches. Try fewer or other words, or leave the query empty to list every block of a kind.")
            : Out(found);
    }

    private static ToolOutcome DescribeBlock(string id)
    {
        if (BlockRegistry.Find(id) is not { } b) return new ToolOutcome($"There is no block \"{id}\". Use find_blocks to look one up.", true);
        return Out(new
        {
            id = b.Id,
            kind = KindName(b.Kind),
            name = b.Name,
            sentence = b.Sentence,
            help = b.Help.Length > 0 ? b.Help : null,
            pickers = b.Params.Any(p => p.Type == ParamType.Cards)
                ? "A card picker goes in \"filters\" under its key. Give only the fields that differ from its defaultFilter; the rest keep those defaults."
                : null,
            options = b.Params.Select(p => new
            {
                key = p.Key,
                label = p.Label,
                type = p.Type switch { ParamType.Integer => "integer", ParamType.Choice => "choice", ParamType.Bool => "bool", _ => "cards" },
                @default = p.Type == ParamType.Cards ? null : p.Default,
                min = p.Type == ParamType.Integer ? p.Min : (int?)null,
                max = p.Type == ParamType.Integer ? p.Max : (int?)null,
                values = p.Type == ParamType.Choice ? p.Choices.Select(c => new { value = c.Value, means = c.Display }) : null,
                defaultFilter = p.Type == ParamType.Cards ? CompactFilter(p.DefaultFilter?.Invoke() ?? new CardFilter()) : null,
                allowsThisCard = p.Type == ParamType.Cards && p.AllowThisCard ? true : (bool?)null,
                // Some options only matter for certain values of others; the editor hides them otherwise.
                conditional = p.ShownWhen is not null ? true : (bool?)null,
            }),
        });
    }

    private static ToolOutcome DescribeModel(string type)
    {
        object? described = type switch
        {
            "card" => Fields(typeof(CardData)),
            "filter" => new
            {
                fields = Fields(typeof(CardFilter)),
                zones = "Zones is a list joined with \", \": deck, hand, monsterZone, spellTrapZone, graveyard, banished, extraDeck (field = monsterZone + spellTrapZone).",
                race = "Race and attribute may hold several values joined with \", \", meaning any of them.",
            },
            "requirement" => new
            {
                fields = Fields(typeof(CardRequirement)),
                ops = Enum.GetNames<RequirementOp>().Select(Camel),
                properties = CardProperties.All.Select(p => new
                {
                    property = p.Key,
                    label = p.Label,
                    kind = p.Kind.ToString(),
                    ops = p.Ops.Select(o => Camel(o.ToString())),
                    values = p.Choices?.Select(c => new { value = c.Value, means = c.Label }),
                }),
                how = "value (and value2 for between/outside) hold numbers; values hold the choices (Attribute, Type, Summon kind...) or archetype codes / passcodes.",
            },
            "effect" => new
            {
                fields = Fields(typeof(EffectEvent)),
                example = JsonSerializer.Deserialize<JsonElement>(EffectJson(EffectTemplates.All[0].Create()), Json),
                notes = "when holds one trigger block, alsoWhen more triggers that start it too, if conditions, cost costs, do actions (or passive blocks when the trigger is when.while_face_up). limit is none, soft (\"Once per turn\"), hard (\"You can only use this effect of X once per turn\"), duel, chain, faceUp (\"Once while face-up on the field\"), faceUpAndHard or softAndHard; times is how many uses it allows (\"Up to twice per turn\").",
            },
            "block" => new
            {
                fields = Fields(typeof(BlockInstance)),
                notes = "blockId names the block; values hold its integer/choice/bool options as strings by key; filters hold its card pickers by key. "
                    + "link says how a DO block follows the one before: and, ifYouDo, then, alsoAfterThat, ifYouDont, option (a new \"● \" option of \"activate 1 of these effects\"). "
                    + "optional makes it \"you can ...\". or (IF blocks after the first) makes it an alternative to the IF block before it (\"... or ...\"). Options left out keep their defaults.",
            },
            "procedure" => new { fields = Fields(typeof(SummonProcedure)), materialSlot = Fields(typeof(MaterialSlot)) },
            _ => null,
        };
        return described is null ? new ToolOutcome("type must be card, filter, requirement, effect, block or procedure.", true) : Out(described);
    }

    /// <summary>The settable JSON fields of a type: name, type, enum values and the default.</summary>
    private static IEnumerable<object> Fields(Type type)
    {
        var defaults = JsonSerializer.SerializeToNode(Activator.CreateInstance(type), Json) as JsonObject;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var name = Camel(p.Name);
            yield return new
            {
                name,
                type = TypeName(t),
                values = t.IsEnum ? Enum.GetNames(t).Select(Camel) : null,
                flags = t.IsEnum && t.GetCustomAttribute<FlagsAttribute>() is not null ? true : (bool?)null,
                @default = defaults?[name]?.ToJsonString(Compact),
            };
        }
    }

    private static string TypeName(Type t) =>
        t == typeof(string) ? "string"
        : t == typeof(bool) ? "boolean"
        : t == typeof(int) || t == typeof(long) ? "integer"
        : t.IsEnum ? "enum"
        : t.IsArray ? $"list of {TypeName(t.GetElementType()!)}"
        : t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? $"list of {TypeName(t.GetGenericArguments()[0])}"
        : t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? $"map of {TypeName(t.GetGenericArguments()[1])}"
        : Camel(t.Name);

    private static ToolOutcome ListTemplates() =>
        Out(EffectTemplates.All.Select(t => new { name = t.Name, like = t.LikeCard, @for = t.For.ToString(), group = t.Group, description = t.Description }));

    private static ToolOutcome GetTemplate(string name) =>
        EffectTemplates.All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } t
            ? new ToolOutcome(EffectJson(t.Create()))
            : new ToolOutcome($"There is no template \"{name}\". list_templates gives their names.", true);

    // ---------------------------------------------------------------- the pack and its cards

    private ToolOutcome ListCards()
    {
        var pack = workspace.Pack;
        return Out(new
        {
            pack = pack.Name,
            selected = workspace.Card is { } c ? pack.Cards.IndexOf(c) : (int?)null,
            cards = pack.Cards.Select((p, i) => new { index = i, passcode = p.Card.Id, name = p.Card.Name, kind = Camel(p.Card.Kind.ToString()), effects = p.Effects.Count, script = p.UseCustomLua ? "hand-written" : "blocks" }),
            archetypes = pack.Archetypes.Select(a => new { code = $"0x{a.Code:x}", value = a.Code, name = a.Name }),
            customTypes = pack.CustomTypes.Select(t => new { value = $"0x{t.Value:x}", name = t.Name }),
        });
    }

    private ToolOutcome SelectCard(int index) =>
        workspace.SelectCard(index)
            ? new ToolOutcome($"Selected \"{workspace.Card!.Card.Name}\".", Activity: $"Selected \"{workspace.Card!.Card.Name}\"")
            : new ToolOutcome($"There is no card {index}; the pack has {workspace.Pack.Cards.Count}.", true);

    private ToolOutcome NewCard(string kind)
    {
        var k = kind.ToLowerInvariant() switch { "spell" => CardKind.Spell, "trap" => CardKind.Trap, _ => CardKind.Monster };
        var project = workspace.AddCard(k);
        return new ToolOutcome($"Added a new {k} with passcode {project.Card.Id}; it is selected.", Activity: $"Added a new {k} card");
    }

    private CardProject Card => workspace.Card ?? throw new InvalidOperationException("The pack has no card selected. Use new_card or select_card first.");

    private ToolOutcome GetCard()
    {
        var project = Card;
        var node = JsonSerializer.SerializeToNode(project, Json)!.AsObject();
        node.Remove("effects");
        node["effects"] = new JsonArray(project.Effects.Select((e, i) =>
        {
            var effect = JsonNode.Parse(EffectJson(e))!.AsObject();
            effect["number"] = i + 1;
            effect["text"] = EffectTextGenerator.Describe(e, project.Card, workspace.Pack);
            return (JsonNode)effect;
        }).ToArray());
        if (project.UseCustomLua is false) node.Remove("customLua");
        node["generatedText"] = EffectTextGenerator.GenerateWithSections(project, workspace.Pack);
        node["problems"] = new JsonArray(Problems(project).Select(p => (JsonNode)p).ToArray());
        return new ToolOutcome(node.ToJsonString(Json));
    }

    private ToolOutcome UpdateCard(JsonElement input)
    {
        var project = Card;
        var changed = new List<string>();
        if (input.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
        {
            project.Card = Merge(project.Card, card);
            changed.AddRange(card.EnumerateObject().Select(p => p.Name));
        }
        if (input.TryGetProperty("procedure", out var proc) && proc.ValueKind == JsonValueKind.Object)
        {
            project.Procedure = Merge(project.Procedure, proc);
            changed.Add("procedure");
        }
        if (input.TryGetProperty("equipFilter", out var equip) && equip.ValueKind == JsonValueKind.Object)
        {
            project.EquipFilter = Merge(new CardFilter(), equip);
            changed.Add("equip targets");
        }
        if (changed.Count == 0) return new ToolOutcome("Give card, procedure or equipFilter with the fields to change.", true);
        workspace.CardChanged();
        return Verdict(project, $"Changed {string.Join(", ", changed)} of \"{project.Card.Name}\"");
    }

    /// <summary>The value with the given JSON fields laid over it; unknown fields are an error rather than ignored.</summary>
    private static T Merge<T>(T current, JsonElement changes)
    {
        var node = JsonSerializer.SerializeToNode(current, Json)!.AsObject();
        var known = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => Camel(p.Name)).ToHashSet();
        foreach (var field in changes.EnumerateObject())
        {
            var name = Camel(field.Name);
            if (!known.Contains(name)) throw new ArgumentException($"\"{field.Name}\" isn't a field of {Camel(typeof(T).Name)}; describe_model lists them.");
            node[name] = JsonNode.Parse(field.Value.GetRawText());
        }
        return node.Deserialize<T>(Json) ?? current;
    }

    // ---------------------------------------------------------------- effects

    private ToolOutcome AddEffect(JsonElement input)
    {
        var project = Card;
        var (effect, errors) = ReadEffect(input.GetProperty("effect"));
        if (effect is null) return new ToolOutcome("The effect wasn't added:\n" + string.Join("\n", errors), true);
        var at = Int(input, "position") is { } p ? Math.Clamp(p - 1, 0, project.Effects.Count) : project.Effects.Count;
        project.Effects.Insert(at, effect);
        workspace.CardChanged();
        return Verdict(project, $"Added effect {at + 1} to \"{project.Card.Name}\"");
    }

    private ToolOutcome ReplaceEffect(JsonElement input)
    {
        var project = Card;
        var number = Int(input, "number") ?? 0;
        if (number < 1 || number > project.Effects.Count) return new ToolOutcome($"The card has {project.Effects.Count} effects; there is no effect {number}.", true);
        var (effect, errors) = ReadEffect(input.GetProperty("effect"));
        if (effect is null) return new ToolOutcome("The effect wasn't changed:\n" + string.Join("\n", errors), true);
        project.Effects[number - 1] = effect;
        workspace.CardChanged();
        return Verdict(project, $"Changed effect {number} of \"{project.Card.Name}\"");
    }

    private ToolOutcome RemoveEffect(int number)
    {
        var project = Card;
        if (number < 1 || number > project.Effects.Count) return new ToolOutcome($"The card has {project.Effects.Count} effects; there is no effect {number}.", true);
        project.Effects.RemoveAt(number - 1);
        workspace.CardChanged();
        return Verdict(project, $"Removed effect {number} of \"{project.Card.Name}\"");
    }

    private ToolOutcome OrganizeCard()
    {
        var project = Card;
        var result = CardOrganizer.Organize(project, workspace.Pack);
        if (result.Skipped is { } why) return new ToolOutcome(why);
        if (!result.Changed) return Verdict(project, $"\"{project.Card.Name}\" is already organized; nothing changed");
        workspace.CardChanged();
        return Verdict(project, $"Organized \"{project.Card.Name}\":\n{result.Summary}");
    }

    /// <summary>
    /// Reads an effect, checking every block against the catalog: the block exists and fits its slot, every option is one
    /// the block has, with an allowed value. Options left out keep the block's defaults, as when the user adds a block.
    /// </summary>
    public static (EffectEvent? Effect, List<string> Errors) ReadEffect(JsonElement el)
    {
        var errors = new List<string>();
        if (el.ValueKind != JsonValueKind.Object) return (null, ["The effect must be a JSON object."]);
        var effect = new EffectEvent { Label = Str(el, "label") ?? "Effect" };
        if (Str(el, "limit") is { } limit)
        {
            if (Enum.TryParse<OncePerTurn>(limit, true, out var l)) effect.Limit = l;
            else errors.Add($"limit \"{limit}\" must be one of {string.Join(", ", Enum.GetNames<OncePerTurn>().Select(Camel))}.");
        }
        effect.SharedLimitGroup = Int(el, "sharedLimitGroup") ?? 0;
        effect.Times = Math.Clamp(Int(el, "times") ?? 1, 1, 9);
        if (el.TryGetProperty("when", out var when) && when.ValueKind == JsonValueKind.Object)
            effect.When = ReadBlock(when, [BlockKind.Trigger], "when", errors);
        else errors.Add("The effect needs a \"when\" block (a trigger). Lasting effects use when.while_face_up.");
        effect.AlsoWhen = ReadBlocks(el, "alsoWhen", [BlockKind.Trigger], errors);
        effect.If = ReadBlocks(el, "if", [BlockKind.Condition], errors);
        effect.Cost = ReadBlocks(el, "cost", [BlockKind.Cost], errors);
        effect.Do = ReadBlocks(el, "do", [BlockKind.Action, BlockKind.Passive], errors);
        return errors.Count == 0 ? (effect, errors) : (null, errors);
    }

    private static List<BlockInstance> ReadBlocks(JsonElement el, string key, BlockKind[] kinds, List<string> errors)
    {
        if (!el.TryGetProperty(key, out var list) || list.ValueKind == JsonValueKind.Null) return [];
        if (list.ValueKind != JsonValueKind.Array) { errors.Add($"\"{key}\" must be a list of blocks."); return []; }
        return list.EnumerateArray().Select((b, i) => ReadBlock(b, kinds, $"{key}[{i}]", errors)).OfType<BlockInstance>().ToList();
    }

    private static BlockInstance? ReadBlock(JsonElement el, BlockKind[] kinds, string where, List<string> errors)
    {
        var id = Str(el, "blockId") ?? Str(el, "block") ?? Str(el, "id") ?? "";
        if (BlockRegistry.Find(id) is not { } def)
        {
            errors.Add($"{where}: there is no block \"{id}\" (find_blocks looks them up).");
            return null;
        }
        if (!kinds.Contains(def.Kind))
        {
            errors.Add($"{where}: {id} is a {KindName(def.Kind)} block and can't go in {where.Split('[')[0]}.");
            return null;
        }
        var inst = def.CreateInstance();
        if (el.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var v in values.EnumerateObject())
            {
                var p = def.Param(v.Name);
                var text = v.Value.ValueKind switch
                {
                    JsonValueKind.String => v.Value.GetString()!,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => v.Value.GetRawText(),
                };
                if (p is null || p.Type == ParamType.Cards)
                {
                    errors.Add($"{where}: {id} has no option \"{v.Name}\"; its options are {string.Join(", ", def.Params.Where(x => x.Type != ParamType.Cards).Select(x => x.Key))}"
                        + (def.Params.Any(x => x.Type == ParamType.Cards) ? $", and the card pickers {string.Join(", ", def.Params.Where(x => x.Type == ParamType.Cards).Select(x => x.Key))} go in \"filters\"." : "."));
                    continue;
                }
                var problem = p.Type switch
                {
                    ParamType.Integer when !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => "must be a whole number",
                    ParamType.Integer when int.Parse(text, CultureInfo.InvariantCulture) is var n && (n < p.Min || n > p.Max) => $"must be from {p.Min} to {p.Max}",
                    ParamType.Bool when text is not ("true" or "false") => "must be true or false",
                    ParamType.Choice when p.Choices.All(c => c.Value != text) => $"must be one of {string.Join(", ", p.Choices.Select(c => $"\"{c.Value}\""))}",
                    _ => null,
                };
                if (problem is null) inst.Values[p.Key] = text;
                else errors.Add($"{where}: {id} option \"{p.Key}\" {problem} (got \"{text}\").");
            }
        }
        if (el.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Object)
        {
            foreach (var f in filters.EnumerateObject())
            {
                if (def.Param(f.Name) is not { Type: ParamType.Cards })
                {
                    errors.Add($"{where}: {id} has no card picker \"{f.Name}\".");
                    continue;
                }
                try
                {
                    inst.Filters[f.Name] = Merge(inst.Filters.GetValueOrDefault(f.Name) ?? new CardFilter(), f.Value);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    errors.Add($"{where}: card picker \"{f.Name}\": {ex.Message}");
                }
            }
        }
        if (Str(el, "link") is { } link)
        {
            if (Enum.TryParse<ActionLink>(link, true, out var l)) inst.Link = l;
            else errors.Add($"{where}: link \"{link}\" must be one of {string.Join(", ", Enum.GetNames<ActionLink>().Select(Camel))}.");
        }
        if (el.TryGetProperty("optional", out var optional) && optional.ValueKind is JsonValueKind.True or JsonValueKind.False) inst.Optional = optional.GetBoolean();
        if (el.TryGetProperty("or", out var or) && or.ValueKind is JsonValueKind.True or JsonValueKind.False) inst.Or = or.GetBoolean();
        return inst;
    }

    // ---------------------------------------------------------------- scripts

    private ToolOutcome CompileCard()
    {
        var project = Card;
        var result = ScriptCompiler.Compile(project, workspace.Pack);
        return Out(new { lua = result.Lua, problems = Problems(project, result), cardText = EffectTextGenerator.GenerateWithSections(project, workspace.Pack) });
    }

    private ToolOutcome TranslateScript(string lua)
    {
        if (string.IsNullOrWhiteSpace(lua)) return new ToolOutcome("Give the script to read.", true);
        var data = workspace.Card?.Card ?? new CardData();
        var id = System.Text.RegularExpressions.Regex.Match(lua, @"function c(\d+)\.initial_effect") is { Success: true } m ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : data.Id;
        var result = ScriptTranslator.Translate(lua, id, data);
        return Out(new
        {
            complete = result.Complete,
            parts = result.Pieces.Select(p => new
            {
                title = p.Piece.Title,
                translated = p.Translated,
                reason = p.Translated ? null : p.Reason,
                effects = p.Effects.Select(e => JsonSerializer.Deserialize<JsonElement>(EffectJson(e), Json)),
                text = p.Effects.Select(e => EffectTextGenerator.Describe(e, data, workspace.Pack)),
            }),
        });
    }

    private ToolOutcome SetScript(JsonElement input)
    {
        var project = Card;
        var useBlocks = input.TryGetProperty("use_blocks", out var u) && u.ValueKind == JsonValueKind.True;
        if (useBlocks)
        {
            project.UseCustomLua = false;
            workspace.CardChanged();
            return Verdict(project, $"\"{project.Card.Name}\" uses its blocks again");
        }
        var lua = Str(input, "lua");
        if (string.IsNullOrWhiteSpace(lua)) return new ToolOutcome("Give the script (lua) to use.", true);
        project.CustomLua = lua;
        project.UseCustomLua = true;
        workspace.CardChanged();
        var problems = ScriptChecker.Check(lua).Select(d => d.ToString()).ToList();
        return Out(new { done = "The card now uses this script.", problems }, $"Gave \"{project.Card.Name}\" a hand-written script");
    }

    // ---------------------------------------------------------------- results

    private ToolOutcome Verdict(CardProject project, string activity) =>
        Out(new
        {
            done = activity + ".",
            effects = project.Effects.Select((e, i) => $"{i + 1}. {EffectTextGenerator.Describe(e, project.Card, workspace.Pack)}"),
            problems = Problems(project),
        }, activity);

    private List<string> Problems(CardProject project, CompileResult? result = null)
    {
        result ??= ScriptCompiler.Compile(project, workspace.Pack);
        var all = result.Diagnostics.Select(d => d.ToString()).ToList();
        if (!result.HasErrors) all.AddRange(ScriptChecker.Check(result.Lua).Select(d => d.ToString()));
        return all;
    }

    /// <summary>
    /// An effect as JSON, with each card picker reduced to the fields that differ from the block's own default picker
    /// (owner and zones always shown), which is how <see cref="ReadEffect"/> reads them back.
    /// </summary>
    public static string EffectJson(EffectEvent effect)
    {
        var node = JsonSerializer.SerializeToNode(effect, Json)!.AsObject();
        foreach (var block in Blocks(node)) if (block["filters"] is JsonObject filters && BlockRegistry.Find((string?)block["blockId"] ?? "") is { } def)
            foreach (var key in filters.Select(f => f.Key).ToList())
                filters[key] = CompactFilter(filters[key]!.Deserialize<CardFilter>(Json)!, def.Param(key)?.DefaultFilter?.Invoke());
        return node.ToJsonString(Json);
    }

    private static IEnumerable<JsonObject> Blocks(JsonObject effect)
    {
        if (effect["when"] is JsonObject w) yield return w;
        foreach (var key in new[] { "alsoWhen", "if", "cost", "do" })
            if (effect[key] is JsonArray list) foreach (var b in list.OfType<JsonObject>()) yield return b;
    }

    private static JsonNode CompactFilter(CardFilter filter, CardFilter? baseline = null)
    {
        var node = JsonSerializer.SerializeToNode(filter, Json)!.AsObject();
        var defaults = JsonSerializer.SerializeToNode(baseline ?? new CardFilter(), Json)!.AsObject();
        foreach (var (key, value) in node.ToList())
            if (key is not ("owner" or "zones") && JsonNode.DeepEquals(value, defaults[key])) node.Remove(key);
        return node;
    }

    private static ToolOutcome Out(object value, string? activity = null) => new(JsonSerializer.Serialize(value, Json), Activity: activity);

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    private static string? Str(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
            : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var s) ? s : null
            : null;
}
