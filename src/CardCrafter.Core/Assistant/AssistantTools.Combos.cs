using System.Globalization;
using System.Text.Json;
using CardCrafter.Core.Combos;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Assistant;

/// <summary>
/// The combo tools: the assistant reads the pack's combos (with the deck's card texts, split into numbered effects) and
/// writes new ones step by step. Every step is checked against the combo's deck: a combo may only use the cards in it.
/// After each change the combo is replayed and the tool answers with each step's problems and the end board.
/// </summary>
public sealed partial class AssistantTools
{
    private const string StepSchema =
        """{"type":"object","properties":{"kind":{"type":"string","enum":["normalSummon","tributeSummon","set","specialSummon","fusionSummon","synchroSummon","xyzSummon","linkSummon","ritualSummon","activateCard","activateEffect","addToHand","draw","sendToGraveyard","banish","returnToHand","returnToDeck","returnToExtraDeck","place","attach","detach","changePosition","interruption","endTurn","note"]},"card":{"type":["string","integer"],"description":"The card's name or passcode. For interruption, the opponent's card."},"copy":{"type":"integer","description":"Which copy, when it matters (1 = first)."},"from":{"type":"string","description":"hand, deck, extraDeck, monsterZone, extraMonsterZone, spellTrapZone, pendulumZone, fieldZone, graveyard, banished, xyzMaterial, field, token. Left out: where the card is."},"to":{"type":"string","description":"Where it goes; left out: where this kind of step puts it (Summons to a Monster Zone, Normal Spells to the GY after resolving, an effect leaves its card where it is)."},"slot":{"type":"integer","description":"Zone 1-5 from the left (1-2 for Extra Monster Zones)."},"position":{"type":"string","enum":["attack","defense","faceDownDefense","faceUp","faceDown"]},"effect":{"type":"integer","description":"Which numbered effect of the card's text it uses (get_combo lists them)."},"materials":{"type":"array","description":"Cards moved with it: materials or Tributes (to the GY, or under the Xyz), and for an effect its costs and what it moved. Each: {card, copy, from, to}.","items":{"type":"object"}},"note":{"type":"string","description":"The explanation the player reads: why this play, what it sets up."}},"required":["kind"]}""";

    /// <summary>The fields of a combo, for create_combo and replace_combo.</summary>
    private const string ComboFields = """
        "name":{"type":"string"},"notes":{"type":"string"},
        "deck_text":{"type":"string","description":"A deck list as the user pasted it (sections Monster/Spell/Trap/Extra/Side, lines like \"3 Ash Blossom & Joyous Spring\") or a .ydk file's text."},
        "copy_deck_from":{"type":"integer","description":"Use the deck of this combo (0-based, list_combos)."},
        "use_pack_cards":{"type":"boolean","description":"Use the open pack's own cards, one copy each, as the deck."},
        "opening_hand":{"type":"array","items":{"type":["string","integer"]},"description":"The cards in the opening hand, by name or passcode, from the Main Deck."},
        "steps":{"type":"array","items":
        """ + StepSchema + "}";

    private static IReadOnlyList<AssistantTool> ComboTools =>
    [
        new("list_combos",
            "The pack's combos (Combos tab): index, name, deck size, number of steps, and how many steps can't be played as written.",
            "{}", []),
        new("get_combo",
            "One combo: its deck (names, passcodes, copies), opening hand, every step with its card, zones, the effect text it used and its note, "
            + "the problems the replay finds, and the end board. card_texts (default true) adds each deck card's text split into numbered effects: "
            + "read them before writing or explaining steps.",
            """{"index":{"type":"integer","description":"0-based, from list_combos."},"card_texts":{"type":"boolean"}}""",
            ["index"]),
        new("create_combo",
            "Adds a combo to the pack. Give the deck (deck_text, copy_deck_from or use_pack_cards), the opening hand and the steps. "
            + "Every card must be in the deck, except the opponent's card of an interruption step. Without steps, the answer lists the deck's "
            + "cards with their effects so the steps can be written next with replace_combo. Answers with the replay: each step's problems and the end board.",
            "{" + ComboFields + "}", []),
        new("replace_combo",
            "Replaces parts of a combo: the fields given (name, notes, deck, opening_hand, steps) replace the old ones; the rest stay. "
            + "steps replaces the whole list. Answers with the replay.",
            """{"index":{"type":"integer"},""" + ComboFields + "}",
            ["index"]),
    ];

    private ToolOutcome ListCombos()
    {
        var catalog = workspace.ComboCards;
        var combos = workspace.Pack.Combos;
        if (combos.Count == 0) return new ToolOutcome("The pack has no combos yet. create_combo adds one.");
        return Out(combos.Select((c, i) =>
        {
            var replay = ComboReplay.Run(c, catalog);
            return new
            {
                index = i,
                name = c.Name,
                deck = $"{c.Deck.Main.Count} Main, {c.Deck.Extra.Count} Extra, {c.Deck.Side.Count} Side",
                steps = c.Steps.Count,
                flaggedSteps = replay.FlaggedCount,
            };
        }));
    }

    private Combo ComboAt(JsonElement input)
    {
        var index = Int(input, "index") ?? -1;
        var combos = workspace.Pack.Combos;
        if (index < 0 || index >= combos.Count)
            throw new ArgumentException($"There is no combo {index}; the pack has {combos.Count} (list_combos).");
        return combos[index];
    }

    private ToolOutcome GetCombo(JsonElement input)
    {
        var combo = ComboAt(input);
        var texts = !(input.TryGetProperty("card_texts", out var t) && t.ValueKind == JsonValueKind.False);
        return Out(Describe(combo, texts));
    }

    private ToolOutcome CreateCombo(JsonElement input)
    {
        var combo = new Combo { Name = Str(input, "name") ?? "New combo" };
        var errors = new List<string>();
        var deckGiven = ReadComboInput(input, combo, errors, requireDeck: true);
        if (errors.Count > 0) return new ToolOutcome("The combo wasn't added:\n" + string.Join("\n", errors), true);
        ComboFile.RememberNames(combo, workspace.ComboCards);
        workspace.Pack.Combos.Add(combo);
        workspace.CombosChanged();
        var index = workspace.Pack.Combos.Count - 1;
        return Out(Describe(combo, combo.Steps.Count == 0 && deckGiven, index), $"Added the combo \"{combo.Name}\"");
    }

    private ToolOutcome ReplaceCombo(JsonElement input)
    {
        var combo = ComboAt(input);
        var copy = PackSerializer.Clone(combo);
        var errors = new List<string>();
        var deckGiven = ReadComboInput(input, copy, errors, requireDeck: false);
        if (errors.Count > 0) return new ToolOutcome("The combo wasn't changed:\n" + string.Join("\n", errors), true);
        ComboFile.RememberNames(copy, workspace.ComboCards);
        var index = workspace.Pack.Combos.IndexOf(combo);
        workspace.Pack.Combos[index] = copy;
        workspace.CombosChanged();
        return Out(Describe(copy, copy.Steps.Count == 0 && deckGiven, index), $"Changed the combo \"{copy.Name}\"");
    }

    /// <summary>Reads the fields given into the combo. Returns whether a new deck was given.</summary>
    private bool ReadComboInput(JsonElement input, Combo combo, List<string> errors, bool requireDeck)
    {
        var catalog = workspace.ComboCards;
        if (Str(input, "name") is { } name) combo.Name = name;
        if (Str(input, "notes") is { } notes) combo.Notes = notes;
        var deckGiven = false;
        if (Str(input, "deck_text") is { Length: > 0 } text)
        {
            var read = DeckImport.Read(text, catalog);
            combo.Deck = read.Deck;
            if (read.Unknown.Count > 0) errors.Add($"These cards of the deck list weren't found: {string.Join(", ", read.Unknown)}. Check the names with the user.");
            deckGiven = true;
        }
        else if (Int(input, "copy_deck_from") is { } from)
        {
            if (from < 0 || from >= workspace.Pack.Combos.Count) errors.Add($"There is no combo {from} to copy the deck from.");
            else
            {
                var source = workspace.Pack.Combos[from];
                combo.Deck = PackSerializer.Clone(source.Deck);
                foreach (var (code, n) in source.Names) combo.Names.TryAdd(code, n);
                deckGiven = true;
            }
        }
        else if (input.TryGetProperty("use_pack_cards", out var usePack) && usePack.ValueKind == JsonValueKind.True)
        {
            combo.Deck = DeckImport.FromPack(workspace.Pack).Deck;
            deckGiven = true;
        }
        if (requireDeck && !deckGiven) errors.Add("Give the deck: deck_text (the user's list or .ydk), copy_deck_from or use_pack_cards.");
        if (combo.Deck.IsEmpty && deckGiven) errors.Add("The deck is empty.");

        if (input.TryGetProperty("opening_hand", out var hand) && hand.ValueKind == JsonValueKind.Array)
        {
            combo.StartingHand = [];
            combo.RandomHand = false;
            foreach (var h in hand.EnumerateArray())
                if (ResolveCard(h, combo, catalog, inMain: true, out var error) is { } code) combo.StartingHand.Add(code);
                else errors.Add($"opening_hand: {error}");
        }
        if (input.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            combo.Steps = [];
            var i = 0;
            foreach (var s in steps.EnumerateArray())
            {
                i++;
                if (ReadStep(s, combo, catalog, $"step {i}", errors) is { } step) combo.Steps.Add(step);
            }
        }
        return deckGiven;
    }

    private static ComboStep? ReadStep(JsonElement el, Combo combo, ComboCatalog catalog, string where, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"{where}: a step must be an object."); return null; }
        if (!TryEnum<ComboStepKind>(Str(el, "kind"), out var kind)) { errors.Add($"{where}: kind \"{Str(el, "kind")}\" isn't a step kind."); return null; }
        var step = new ComboStep { Kind = kind, Note = Str(el, "note") ?? "", Effect = Int(el, "effect") ?? 0, Copy = Int(el, "copy") ?? 0, Slot = Int(el, "slot") ?? 0 };
        var count = errors.Count;
        step.From = Zone(el, "from", where, errors);
        step.To = Zone(el, "to", where, errors);
        step.Position = Position(el, where, errors);
        if (el.TryGetProperty("card", out var card) && card.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            // The opponent's card and a Token aren't in the deck; every other card must be.
            var any = kind == ComboStepKind.Interruption || step.From == ComboZone.Token;
            if (ResolveCard(card, combo, catalog, inMain: false, out var error, anyCard: any) is { } code)
            {
                step.Code = code;
                if (step.From == ComboZone.Token && catalog.Find(code)?.Data is { } d && !(d.IsMonster && d.Frame == MonsterFrame.Token))
                    errors.Add($"{where}: {catalog.Name(code)} isn't a Token.");
            }
            else errors.Add($"{where}: {error}");
        }
        else if (ComboRules.NeedsCard(kind) && kind != ComboStepKind.Draw) errors.Add($"{where}: give the card.");
        if (step.Effect > 0 && step.Code != 0 && catalog.Find(step.Code) is { } info && CardEffects.Split(info.Text).All(p => p.Number != step.Effect))
            errors.Add($"{where}: {info.Name} has no effect {step.Effect}; its effects are numbered 1 to {CardEffects.Split(info.Text).Count(p => p.IsEffect)}.");
        if (el.TryGetProperty("materials", out var mats) && mats.ValueKind == JsonValueKind.Array)
        {
            var j = 0;
            foreach (var m in mats.EnumerateArray())
            {
                j++;
                var at = $"{where} material {j}";
                if (!m.TryGetProperty("card", out var mc)) { errors.Add($"{at}: give the card."); continue; }
                if (ResolveCard(mc, combo, catalog, inMain: false, out var error) is not { } mcode) { errors.Add($"{at}: {error}"); continue; }
                step.Materials.Add(new ComboMove
                {
                    Code = mcode, Copy = Int(m, "copy") ?? 0, Slot = Int(m, "slot") ?? 0,
                    From = Zone(m, "from", at, errors), To = Zone(m, "to", at, errors), Position = Position(m, at, errors),
                });
            }
        }
        return errors.Count == count ? step : null;
    }

    /// <summary>A card by name or passcode, from the combo's deck (unless any card will do).</summary>
    private static long? ResolveCard(JsonElement value, Combo combo, ComboCatalog catalog, bool inMain, out string error, bool anyCard = false)
    {
        error = "";
        var deck = (inMain ? combo.Deck.Main : combo.Deck.Playable).Select(catalog.Canonical).ToHashSet();
        long? code = null;
        string written = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) code = catalog.Canonical(n);
        else if (long.TryParse(written.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) code = catalog.Canonical(parsed);
        else
        {
            var key = ComboCatalog.NameKey(written);
            code = deck.Cast<long?>().FirstOrDefault(c => ComboCatalog.NameKey(catalog.Name(c!.Value, combo.Names)) == key)
                ?? catalog.FindByName(written)?.Id;
            if (code is null)
            {
                error = $"no card is named \"{written}\".";
                return null;
            }
        }
        if (!anyCard && !deck.Contains(code.Value))
        {
            error = $"{catalog.Name(code.Value, combo.Names)} isn't in the combo's {(inMain ? "Main Deck" : "deck")}. Use only the deck's cards; if the line needs another card, tell the user instead.";
            return null;
        }
        return code;
    }

    private static ComboZone Zone(JsonElement el, string key, string where, List<string> errors)
    {
        var text = Str(el, key);
        if (text is null) return ComboZone.Auto;
        var t = text.Replace("_", "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
        ComboZone? alias = t switch
        {
            "gy" or "grave" or "cemetery" => ComboZone.Graveyard,
            "banishment" or "removed" => ComboZone.Banished,
            "emz" or "extramonster" => ComboZone.ExtraMonsterZone,
            "mmz" or "monster" or "monsters" => ComboZone.MonsterZone,
            "st" or "spelltrap" or "szone" => ComboZone.SpellTrapZone,
            "pendulum" or "pzone" => ComboZone.PendulumZone,
            "field" or "onfield" => ComboZone.Field,
            "extra" or "ed" => ComboZone.ExtraDeck,
            "material" or "materials" or "overlay" => ComboZone.XyzMaterial,
            _ => null,
        };
        if (alias is { } a) return a;
        if (TryEnum<ComboZone>(t, out var zone)) return zone;
        errors.Add($"{where}: {key} \"{text}\" isn't a zone.");
        return ComboZone.Auto;
    }

    private static ComboPosition Position(JsonElement el, string where, List<string> errors)
    {
        var text = Str(el, "position");
        if (text is null) return ComboPosition.Auto;
        var t = text.Replace("_", "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
        if (t is "atk") return ComboPosition.Attack;
        if (t is "def") return ComboPosition.Defense;
        if (t is "set") return ComboPosition.FaceDown;
        if (TryEnum<ComboPosition>(t, out var p)) return p;
        errors.Add($"{where}: position \"{text}\" must be attack, defense, faceDownDefense, faceUp or faceDown.");
        return ComboPosition.Auto;
    }

    private static bool TryEnum<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        return text is not null && Enum.TryParse(text.Replace("_", "").Replace(" ", ""), true, out value) && Enum.IsDefined(value);
    }

    /// <summary>A combo as the assistant reads it: the steps with what the replay made of them, and optionally the deck's card texts.</summary>
    private object Describe(Combo combo, bool cardTexts, int? index = null)
    {
        var catalog = workspace.ComboCards;
        string Name(long c) => catalog.Name(c, combo.Names);
        string Camel(Enum e) => JsonNamingPolicy.CamelCase.ConvertName(e.ToString());
        var replay = ComboReplay.Run(combo, catalog);
        static IEnumerable<object> Entries(IEnumerable<long> codes, Func<long, string> name) =>
            codes.GroupBy(c => c).Select(g => (object)new { name = name(g.Key), passcode = g.Key, copies = g.Count() });
        return new
        {
            index,
            name = combo.Name,
            notes = combo.Notes.Length > 0 ? combo.Notes : null,
            deck = new { main = Entries(combo.Deck.Main, Name), extra = Entries(combo.Deck.Extra, Name), side = Entries(combo.Deck.Side, Name) },
            openingHand = replay.Start.Hand.Select(c => Name(c.Code)),
            randomHand = combo.RandomHand ? true : (bool?)null,
            startProblems = replay.StartProblems.Count > 0 ? replay.StartProblems : null,
            steps = combo.Steps.Select((s, i) => new
            {
                number = i + 1,
                text = ComboText.Describe(s, Name),
                kind = Camel(s.Kind),
                card = s.Code != 0 ? Name(s.Code) : null,
                from = s.From != ComboZone.Auto ? Camel(s.From) : null,
                to = s.To != ComboZone.Auto ? Camel(s.To) : null,
                effect = s.Effect > 0 ? s.Effect : (int?)null,
                effectText = s.Effect > 0 && catalog.Find(s.Code) is { } c ? CardEffects.Effect(c.Text, s.Effect) : null,
                materials = s.Materials.Count > 0
                    ? s.Materials.Select(m => new { card = Name(m.Code), from = Camel(m.From), to = Camel(m.To) })
                    : null,
                note = s.Note.Length > 0 ? s.Note : null,
                moved = replay.Steps[i].Moves.Select(m => ComboText.Moved(m, Name)),
                problems = replay.Steps[i].IsFlagged ? replay.Steps[i].Problems : null,
            }),
            endBoard = ComboText.EndBoard(replay.End, Name),
            cards = cardTexts
                ? combo.Deck.Playable.Concat(combo.Deck.Side).Distinct().Select(code => catalog.Find(code) is { } card
                    ? (object)new
                    {
                        name = card.Name,
                        passcode = code,
                        type = TypeOf(card.Data),
                        effects = CardEffects.Split(card.Text).Select(p => p.IsEffect ? $"{p.Number}. {p.Text}" : $"({p.Text})"),
                    }
                    : new { name = Name(code), passcode = code, type = "unknown: this card isn't installed, so its text can't be read" })
                : null,
        };
    }

    private static string TypeOf(CardData d) => d.Kind switch
    {
        CardKind.Spell => $"{d.SpellKind} Spell",
        CardKind.Trap => $"{d.TrapKind} Trap",
        _ => $"Level/Rank {d.Level} {d.Frame}{(d.IsPendulum ? " Pendulum" : "")} Monster",
    };
}
