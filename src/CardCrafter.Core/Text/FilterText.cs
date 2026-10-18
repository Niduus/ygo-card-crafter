using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>Turns a card filter into card-text English, e.g. "Level 4 or lower Warrior monster from your Deck".</summary>
public static class FilterText
{
    public static bool IsFieldOnly(CardFilter f) => f.Zones != Zone.None && (f.Zones & ~Zone.Field) == Zone.None;

    /// <summary>", except "Card Name"" for filters that leave cards out, or "".</summary>
    public static string ExceptText(CardFilter f, CardPack? pack, string? ownName)
    {
        var except = new List<string>();
        if (f.ExceptOwnName) except.Add(ownName is { Length: > 0 } n ? $"\"{n}\"" : "cards with this card's name");
        if (f.ExceptCode != 0)
            except.Add(CardNames.Find(f.ExceptCode, pack) is { } en ? $"\"{en}\"" : $"passcode {f.ExceptCode}");
        // ", except Equip Spells": the Spell/Trap kinds a requirement leaves out.
        foreach (var r in f.Requirements.Where(r => SaidAsExcept(f, r)))
            except.Add(string.Join(" or ", Compiler.CardProperties.Find(r.Property)!.Choices!.Where(c => r.Values.Contains(c.Value)).Select(c => c.Label + "s")));
        // Kinds left out, each with its own words: ""Exodia" Monster Cards whose original Levels are 10 or higher, and "Millennium" Monster Cards".
        var kinds = (f.Except ?? []).Select(x => Describe(x, pack, plural: true, withLocation: false, ownName: ownName)
                .Replace(" Level is ", " Levels are ", StringComparison.Ordinal).Replace(" Rank is ", " Ranks are ", StringComparison.Ordinal))
            .Where(t => t.Length > 0).ToList();
        if (kinds.Count > 0) except.Add(kinds.Count == 1 ? kinds[0] : string.Join(", ", kinds.Take(kinds.Count - 1)) + ", and " + kinds[^1]);
        return except.Count == 0 || f.IsSpecific() ? "" : ", except " + string.Join(" or ", except);
    }

    /// <summary>A requirement the card text says with "except": a Spell/Trap kind the cards are not (", except Equip Spells").</summary>
    private static bool SaidAsExcept(CardFilter f, CardRequirement r) =>
        !f.AnyRequirement && r.Property == Compiler.CardProperties.SpellTrapKind && r.Op == RequirementOp.IsNot && r.Values.Count > 0;

    /// <param name="withExcept">False when the caller puts ", except ..." at the end of its own sentence, as cards print it.</param>
    private static string Lv(CardFilter f) => f.MonsterKind == MonsterKind.Xyz ? "Rank" : "Level";

    /// <param name="beforeLocation">Words about the cards as a group that go right before where they are: "with different names".</param>
    public static string Describe(CardFilter f, CardPack? pack, bool plural = false, string preposition = "from", bool withLocation = true, string? ownName = null, bool withExcept = true,
        string? beforeLocation = null)
    {
        if (f.ThisCard) return "this card";
        // "That card": blocks say it in their own words (Blocks/CardRefs.cs); this is how a picker alone reads.
        if (f.That != CardRef.None) return f.That switch
        {
            CardRef.Target => plural ? "the targets" : "the target",
            CardRef.Affected => plural ? "those cards" : "that card",
            CardRef.Cost => plural ? "the cards used for the cost" : "the card used for the cost",
            CardRef.BattleTarget => "the monster this card is battling",
            CardRef.Equipped => "the equipped monster",
            CardRef.Event => Blocks.CardRefs.EventNoun(f, plural),
            CardRef.Attacker => "the attacking monster",
            CardRef.Activated => f.CardType == FilterCardType.Monster ? "that monster" : "that card",
            CardRef.Battlers => "the battling monsters",
            CardRef.BanishedByThis => Blocks.CardRefs.BanishedNoun(f, plural),
            _ => "your battling monster",
        };
        // "1 "Ancient Gear" monster or 1 "Geartown" from your Deck": both kinds, then where they are.
        if (f.OrCards is { } alt)
        {
            var main = f.Clone();
            main.OrCards = null;
            // Angelechy Castellan: "1 monster your opponent controls in this card's adjacent column, or 1 Spell/Trap your opponent
            // controls that is 2 columns away from this card's": with a column, each kind says where it is itself.
            if (withLocation && IsFieldOnly(main) && !main.AnyRequirement && !alt.AnyRequirement && main.Requirements.Concat(alt.Requirements).Any(InColumn))
            {
                var kind = Compiler.FilterCompiler.Alternative(f, alt);
                // Its own zone, when a requirement names one ("in a Spell & Trap Zone").
                if (kind.Requirements.FirstOrDefault(r => r is { Property: "location", Op: RequirementOp.Is } && r.Values is [0x4 or 0x8]) is { } zone)
                {
                    kind.Zones = zone.Values[0] == 0x8 ? Zone.SpellTrapZone : Zone.MonsterZone;
                    kind.Requirements.Remove(zone);
                }
                var first = Describe(main, pack, plural, preposition, true, ownName, withExcept: false);
                var second = Describe(kind, pack, plural, preposition, true, ownName, withExcept: false);
                return (plural ? $"{first} and/or {second}" : $"{first}, or 1 {second}") + (withExcept ? ExceptText(f, pack, ownName) : "");
            }
            var one = Describe(main, pack, plural, preposition, withLocation: false, ownName, withExcept: false);
            var other = alt.Clone();
            other.OrCards = null;
            other.Zones = Zone.None;
            other.ExcludeThisCard = other.ExceptOwnName = false;
            other.ExceptCode = 0;
            var two = Describe(other, pack, plural, preposition, withLocation: false, ownName, withExcept: false);
            var both = plural ? $"{one} and/or {two}" : $"{one} or 1 {two}";
            if (beforeLocation is { Length: > 0 }) both += " " + beforeLocation;
            if (withLocation && Location(main, preposition, plural) is { Length: > 0 } at) both += " " + at;
            return withExcept ? both + ExceptText(f, pack, ownName) : both;
        }
        // "with a Level 4 or lower, or a Tuner": requirements of an "or" group, each group said as one.
        if (!f.AnyRequirement && f.Requirements.Any(r => r.Group > 0))
        {
            var rest = f.Clone();
            rest.Requirements.RemoveAll(r => r.Group > 0);
            var groups = f.Requirements.Where(r => r.Group > 0).GroupBy(r => r.Group)
                .Select(g => g.Select(r => Compiler.CardProperties.Text(r, pack)).Where(t => t.Length > 0).Select(t => plural ? Plural(t) : t).ToList())
                .Where(g => g.Count > 0).Select(g => g.Count == 1 ? g[0] : string.Join(", ", g.Take(g.Count - 1)) + (g.Count > 2 ? "," : "") + " or " + g[^1]).ToList();
            var said = string.Join(" and ", groups);
            if (beforeLocation is { Length: > 0 }) said = said.Length == 0 ? beforeLocation : said + " " + beforeLocation;
            return Describe(rest, pack, plural, preposition, withLocation, ownName, withExcept, said.Length == 0 ? null : said);
        }
        // Snake-Eyes Doomed Dragon: "2 face-up Monster Cards from your Spell & Trap Zone": monsters placed there as Spells.
        if (f is { Zones: Zone.SpellTrapZone, CardType: FilterCardType.Any, Owner: CardOwner.You, AnyRequirement: false }
            && f.Requirements.FindIndex(r => r is { Property: "orig_card_type", Op: RequirementOp.Is } && r.Values.SequenceEqual([0x1L])) is var mi and >= 0)
        {
            var rest = f.Clone();
            rest.Requirements.RemoveAt(mi);
            rest.CardType = FilterCardType.SpellTrap;
            var mon = Describe(rest, pack, plural, preposition, withLocation: false, ownName, withExcept, beforeLocation);
            mon = mon.Replace(plural ? "Spells/Traps" : "Spell/Trap", plural ? "Monster Cards" : "Monster Card", StringComparison.Ordinal);
            return withLocation ? $"{mon} {(preposition.Length == 0 ? "" : preposition + " ")}your Spell & Trap Zone" : mon;
        }
        var words = new List<string>();
        // "a card in your other Pendulum Zone": "other" goes with the zone there.
        if (f.ExcludeThisCard && !OtherPendulumZone(f)) words.Add("other");
        if (f.FaceDownOnly || f.Position == FilterPosition.FaceDownDefense) words.Add(f.Position == FilterPosition.FaceDownDefense ? "face-down Defense Position" : "face-down");
        else if (f.FaceUpOnly || f.Position is FilterPosition.FaceUpAttack or FilterPosition.FaceUpDefense) words.Add("face-up");
        if (f.Position is FilterPosition.Attack or FilterPosition.FaceUpAttack) words.Add("Attack Position");
        else if (f.Position is FilterPosition.Defense or FilterPosition.FaceUpDefense) words.Add("Defense Position");
        if (f.Summoned == FilterSummoned.Special) words.Add("Special Summoned");
        else if (f.Summoned == FilterSummoned.Tribute) words.Add("Tribute Summoned");
        // "non-Tuner WIND monsters": a "non-" kind comes first, before the Level and Attribute.
        if (f.CardCode == 0 && KindNoun(f.MonsterKind) is { Non: true } non) words.Add(non.Noun);
        // So do the requirements cards say that way: "non-Fairy monsters", "non-"Burning Abyss" monster", "non-Link Monster".
        var prefixed = f.CardCode != 0 || f.AnyRequirement ? [] : f.Requirements.Where(r => NonWord(r, pack) is not null).ToList();
        words.AddRange(prefixed.Select(r => NonWord(r, pack)!));
        if (f.LevelMin is { } lo && f.LevelMax is { } hi)
            words.Add(lo == hi ? $"{Lv(f)} {lo}" : $"{Lv(f)} {lo} to {hi}");
        else if (f.LevelMax is { } max) words.Add($"{Lv(f)} {max} or lower");
        // "Level 1 or higher" is any Level: "a monster with a Level", said after the noun.
        else if (f.LevelMin is { } min && !(min <= 1 && Lv(f) == "Level")) words.Add($"{Lv(f)} {min} or higher");
        // A Monster Card's Level reads as a Level does: "a Level 5 or higher Monster Card".
        var cardLevels = f.CardCode != 0 || f.AnyRequirement ? [] : f.Requirements.Where(r => r.Property == "card_level" && CardLevelWords(r) is not null).ToList();
        words.AddRange(cardLevels.Select(r => CardLevelWords(r)!));
        // "EARTH or LIGHT", "Aqua, Fish, or Sea Serpent", ""Ember" or "Flame"": choices of the requirements, said as the fields say theirs.
        var described = f.CardCode != 0 || f.AnyRequirement ? [] : f.Requirements.Where(r => r.Op == RequirementOp.Is && r.Property is "attribute" or "race" or "archetype").ToList();
        described.RemoveAll(prefixed.Contains);
        void Choices(string property) => words.AddRange(described.Where(r => r.Property == property).Select(r => ChoiceWords(r, pack)));
        if (f.Attribute != CardAttribute.None) words.Add(EnumNames.Display(f.Attribute));
        Choices("attribute");
        if (f.Race != CardRace.None) words.Add(pack?.TypeName(f.Race) ?? EnumNames.Display(f.Race));
        Choices("race");
        string Arch(int code) => $"\"{(pack is not null ? pack.ArchetypeName(code) : ArchetypeNames.Find(code) ?? $"0x{code:x}")}\"";
        if (f.Archetype != 0 && f.Archetype2 != 0) words.Add($"{Arch(f.Archetype)} or {Arch(f.Archetype2)}");
        else if (f.Archetype != 0 || f.Archetype2 != 0) words.Add(Arch(f.Archetype != 0 ? f.Archetype : f.Archetype2));
        Choices("archetype");
        // "Fusion, Synchro, or Xyz Monster": the kinds a requirement picks, as the noun.
        var kinds = f.CardCode != 0 || f.AnyRequirement || f.MonsterKind != MonsterKind.Any || f.SpellTrapKind != SpellTrapKind.Any ? null
            : f.Requirements.FirstOrDefault(r => r.Op == RequirementOp.Is && r.Property == "monster_type" && KindsNoun(r) is not null);
        if (kinds is not null) described.Add(kinds);

        string noun;
        if (f.CardCode != 0)
        {
            string Named(long code) => CardNames.Find(code, pack) is { } n ? $"\"{n}\"" : $"{(plural ? "cards" : "card")} with passcode {code}";
            var names = new[] { f.CardCode }.Concat(f.AltCodes).Select(Named).ToList();
            noun = names.Count switch { 1 => names[0], 2 => $"{names[0]} or {names[1]}", _ => string.Join(", ", names.Take(names.Count - 1)) + ", or " + names[^1] };
            words.Clear();
            if (f.ExcludeThisCard) words.Add("other");
        }
        else
        {
            // "Continuous Spell", "Equip Spell", "Field Spell", "Counter Trap".
            static string? KindWord(SpellTrapKind k) => k switch
            {
                SpellTrapKind.Normal => "Normal", SpellTrapKind.Continuous => "Continuous", SpellTrapKind.QuickPlay => "Quick-Play",
                SpellTrapKind.Field => "Field", SpellTrapKind.Equip => "Equip", SpellTrapKind.Ritual => "Ritual", SpellTrapKind.Counter => "Counter", _ => null,
            };
            // "Normal or Quick-Play Spell": a second kind it may be instead.
            var stKind = KindWord(f.SpellTrapKind) is { } k1 && KindWord(f.SpellTrapKind2) is { } k2 && k1 != k2 ? $"{k1} or {k2}"
                : KindWord(f.SpellTrapKind) ?? KindWord(f.SpellTrapKind2);
            noun = stKind is not null && f.CardType is FilterCardType.Spell or FilterCardType.Trap or FilterCardType.SpellTrap or FilterCardType.Any
                ? $"{stKind} {(f.CardType == FilterCardType.Trap || f.SpellTrapKind == SpellTrapKind.Counter ? "Trap" : f.CardType == FilterCardType.Spell || f.SpellTrapKind is SpellTrapKind.QuickPlay or SpellTrapKind.Field or SpellTrapKind.Equip or SpellTrapKind.Ritual || f.SpellTrapKind2 is SpellTrapKind.QuickPlay or SpellTrapKind.Field or SpellTrapKind.Equip or SpellTrapKind.Ritual ? "Spell" : "Spell/Trap")}"
                : KindNoun(f.MonsterKind) is { Non: false } kind ? kind.Noun : kinds is not null ? KindsNoun(kinds)! : f.CardType switch
            {
                FilterCardType.Monster => f.MonsterCards ? "Monster Card" : "monster",
                FilterCardType.Spell => "Spell",
                FilterCardType.Trap => "Trap",
                FilterCardType.SpellTrap => "Spell/Trap",
                // Only the Monster Zones hold only monsters; only the Spell & Trap Zones, only Spells and Traps.
                _ when f.Zones == Zone.MonsterZone => "monster",
                _ when f.Zones == Zone.SpellTrapZone => "Spell/Trap",
                // ATK, DEF and the numbers only monsters have: "a card with 1000 or more ATK" is a monster.
                _ => (f.Race != CardRace.None || f.Attribute != CardAttribute.None || f.LevelMin is not null || f.LevelMax is not null
                      || f.MonsterKind != MonsterKind.Any || f.AtkMin is not null || f.AtkMax is not null || f.DefMin is not null || f.DefMax is not null
                      || (f.AnyRequirement ? f.Requirements.Count > 0 && f.Requirements.All(MonsterOnly) : f.Requirements.Any(MonsterOnly))) ? "monster" : "card",
            };
            // "Spells/Traps", as card text writes several of them.
            if (plural) noun = noun.EndsWith("Spell/Trap", StringComparison.Ordinal) ? noun[..^"Spell/Trap".Length] + "Spells/Traps" : noun + "s";
        }
        words.Add(noun);
        // Omega Vidolium: "3 monsters Special Summoned from the Extra Deck with 3000 or more ATK".
        if (f.Summoned == FilterSummoned.FromExtra) words.Add("Special Summoned from the Extra Deck");
        if (f.CardCode == 0 && f.LevelMin is <= 1 && f.LevelMax is null && Lv(f) == "Level") words.Add("with a Level");

        if (f.AtkMin is { } amin && f.AtkMax is { } amax) words.Add(amin == amax ? $"with {amin} ATK" : $"with {amin} to {amax} ATK");
        else if (f.AtkMax is { } amx) words.Add($"with {amx} or less ATK");
        else if (f.AtkMin is { } amn) words.Add($"with {amn} or more ATK");
        // "with 0 ATK and 0 DEF".
        var withAtk = words.Count > 0 && words[^1].StartsWith("with ", StringComparison.Ordinal) && words[^1].EndsWith(" ATK", StringComparison.Ordinal);
        string? def = f.DefMin is { } dmn2 && f.DefMax is { } dmx2 ? (dmn2 == dmx2 ? $"{dmn2} DEF" : $"{dmn2} to {dmx2} DEF")
            : f.DefMax is { } dmx ? $"{dmx} or less DEF" : f.DefMin is { } dmn ? $"{dmn} or more DEF" : null;
        if (def is not null)
        {
            if (withAtk) words[^1] += " and " + def;
            else words.Add("with " + def);
        }
        // Further requirements: "with 2000 or more original ATK that is not DARK and whose effects are negated" — the
        // "with" ones first, as card text puts them right after the noun.
        // "with the declared Type" comes after where they are, as cards say it: "all face-up monsters on the field with the
        // declared Type", but "1 card with the declared name from your Deck". So does "that was remembered".
        var declared = f.AnyRequirement || !withLocation || Location(f, preposition, plural).StartsWith("from ", StringComparison.Ordinal) ? []
            : f.Requirements.Where(r => Compiler.CardProperties.IsDeclared(r.Property) || r.Property is "remembered" or "ref_mentioned" || InColumn(r)).ToList();
        var extra = f.Requirements.Except(prefixed).Except(described).Except(declared).Except(cardLevels).Where(r => !SaidAsExcept(f, r))
            .Select(r => Compiler.CardProperties.Text(r, pack)).Where(t => t.Length > 0)
            .Select(t => plural ? Plural(t) : t).ToList();
        static bool IsWith(string t) => t.StartsWith("with ", StringComparison.Ordinal) || t.StartsWith("equipped with ", StringComparison.Ordinal);
        static string Join(List<string> parts, string word) => parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]} {word} {parts[1]}",
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + $", {word} " + parts[^1],
        };
        if (f.AnyRequirement)
        {
            // "Any of these": one list joined with "or", in the order they were made.
            if (extra.Count > 0) words.Add(Join(extra, "or"));
        }
        else
        {
            var withs = extra.Where(IsWith).ToList();
            var others = extra.Where(t => !IsWith(t)).ToList();
            if (withs.Count > 0) words.Add(Join(withs, "and"));
            if (others.Count > 0) words.Add(Join(others, "and"));
        }
        if (f.Summoned == FilterSummoned.ThisTurn) words.Add("Summoned this turn");
        if (f.MentionsCode != 0)
        {
            var mentioned = CardNames.Find(f.MentionsCode, pack) is { } mn ? $"\"{mn}\"" : $"passcode {f.MentionsCode}";
            words.Add(f.MentionsOrIs ? $"that is {mentioned} or mentions it" : $"that mentions {mentioned}");
        }

        if (beforeLocation is { Length: > 0 }) words.Add(beforeLocation);
        if (withLocation)
        {
            var where = Location(f, preposition, plural);
            // Angelechy Verdict: "1 "Angelechy" monster with a different name than the cards in your Spell & Trap Zone, from your Extra Deck".
            if (where.StartsWith("from ", StringComparison.Ordinal) && words.Count > 0 && !f.AnyRequirement && f.Requirements.Any(r => r.Property == "unlike_names")) words[^1] += ",";
            if (where.Length > 0) words.Add(where);
        }
        words.AddRange(declared.Select(r => Compiler.CardProperties.Text(r, pack)).Where(t => t.Length > 0).Select(t => plural ? Plural(t) : t));
        var text = string.Join(" ", words);
        // "1 Warrior monster from your Deck, except "Card Name"".
        return withExcept ? text + ExceptText(f, pack, ownName) : text;
    }

    /// <summary>
    /// "non-Fairy", "non-DARK", "non-"Burning Abyss"", "non-Link": a requirement that leaves out one Type, Attribute,
    /// archetype or monster card type, as cards write it before the noun. "An Attribute other than EARTH" is "non-EARTH" too.
    /// </summary>
    private static string? NonWord(CardRequirement r, CardPack? pack)
    {
        if (r.Op == RequirementOp.Is && r.Property == "attribute" && r.Values.Count == 6 && r.Values.Distinct().Count() == 6)
        {
            var missing = Enum.GetValues<CardAttribute>().Where(a => a != CardAttribute.None && !r.Values.Contains((long)a)).ToList();
            return missing.Count == 1 ? $"non-{EnumNames.Display(missing[0])}" : null;
        }
        if (r.Op != RequirementOp.IsNot || r.Values.Count != 1) return null;
        var v = r.Values[0];
        return r.Property switch
        {
            "race" when Enum.IsDefined((CardRace)v) => $"non-{pack?.TypeName((CardRace)v) ?? EnumNames.Display((CardRace)v)}",
            "attribute" when Enum.IsDefined((CardAttribute)v) => $"non-{EnumNames.Display((CardAttribute)v)}",
            "archetype" when v != 0 => $"non-\"{(pack is not null ? pack.ArchetypeName((int)v) : ArchetypeNames.Find((int)v) ?? $"0x{v:x}")}\"",
            "monster_type" when Compiler.CardProperties.Find("monster_type")!.Choices!.FirstOrDefault(c => c.Value == v).Label is { } label =>
                "non-" + label.Replace(" Monster", "", StringComparison.Ordinal).Replace(" monster", "", StringComparison.Ordinal),
            _ => null,
        };
    }

    /// <summary>"Level 5 or higher", "Level 4 or lower", "Level 7": a Monster Card's Level requirement as cards put it before the noun.</summary>
    private static string? CardLevelWords(CardRequirement r)
    {
        var (lo, hi) = (Math.Min(r.Value, r.Value2), Math.Max(r.Value, r.Value2));
        return r.Op switch
        {
            RequirementOp.GreaterOrEqual => $"Level {r.Value} or higher",
            RequirementOp.LessOrEqual => $"Level {r.Value} or lower",
            RequirementOp.Equal => $"Level {r.Value}",
            RequirementOp.Between => $"Level {lo} to {hi}",
            _ => null,
        };
    }

    /// <summary>"EARTH or LIGHT", "Aqua, Fish, or Sea Serpent", ""Ember" or "Flame"": the choices of an Attribute, Type or archetype requirement.</summary>
    private static string ChoiceWords(CardRequirement r, CardPack? pack)
    {
        var names = r.Property switch
        {
            "archetype" => r.Values.Where(v => v != 0).Select(v => $"\"{(pack is not null ? pack.ArchetypeName((int)v) : ArchetypeNames.Find((int)v) ?? $"0x{v:x}")}\"").ToList(),
            "race" => r.Values.Select(v => pack?.TypeName((CardRace)v) ?? EnumNames.Display((CardRace)v)).ToList(),
            _ => r.Values.Select(v => EnumNames.Display((CardAttribute)v)).ToList(),
        };
        return names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} or {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + ", or " + names[^1],
        };
    }

    /// <summary>"Fusion, Synchro, or Xyz Monster" for a requirement that picks kinds whose names end in "Monster"; null for others.</summary>
    private static string? KindsNoun(CardRequirement r)
    {
        var choices = Compiler.CardProperties.Find("monster_type")!.Choices!;
        var labels = r.Values.Select(v => choices.FirstOrDefault(c => c.Value == v).Label).ToList();
        if (labels.Count == 0 || labels.Any(l => l is null || !l.EndsWith(" Monster", StringComparison.Ordinal))) return null;
        var kinds = labels.Select(l => l![..^" Monster".Length]).ToList();
        return (kinds.Count switch
        {
            1 => kinds[0],
            2 => $"{kinds[0]} or {kinds[1]}",
            _ => string.Join(", ", kinds.Take(kinds.Count - 1)) + ", or " + kinds[^1],
        }) + " Monster";
    }

    /// <summary>
    /// A requirement about where the card is beside others: "in this card's adjacent column", "in another column", "in the
    /// same column as ...", "in its adjacent Monster Zones ...". Cards say it after who controls them.
    /// </summary>
    private static bool InColumn(CardRequirement r) => r.Property is Compiler.CardProperties.ColumnsAway or Compiler.CardProperties.SameColumn or "ref_adjacent" or "ref_column";

    /// <summary>A requirement's words for several cards: "that are face-down", "that were Summoned this turn".</summary>
    private static string Plural(string text)
    {
        foreach (var (one, many) in new[] { ("that is ", "that are "), ("that was ", "that were "), ("that has ", "that have "), ("that does ", "that do "), ("that points ", "that point "),
                     ("with a different name ", "with different names ") })
            text = text.Replace(one, many, StringComparison.Ordinal);
        return text;
    }

    /// <summary>A requirement only a monster can meet: a number it has, or a monster card type it is.</summary>
    private static bool MonsterOnly(CardRequirement r) =>
        r.Property is "atk" or "def" or "base_atk" or "base_def" or "level" or "orig_level" or "rank" or "link" or "materials" or "linked"
        || r.Op == RequirementOp.Is && r.Property is "race" or "attribute" or "monster_type" or "orig_race" or "orig_attribute" or "has_materials" or "is_linked"
            or "summon" or "summoned_from" or "summoned_turn" or "spsummoned_turn" or "attacked_turn" or "battled_turn" or "changed_atk"
        || r.Op == RequirementOp.Is && r.Property == "card_type" && r.Values.All(v => v == 0x1);

    /// <summary>
    /// The card-text name of a monster kind: "Effect Monster", "Tuner", "Flip monster". "Non-" kinds give the word
    /// that goes before the rest ("non-Tuner") and the noun stays "monster".
    /// </summary>
    private static (string Noun, bool Non)? KindNoun(MonsterKind kind) => kind switch
    {
        MonsterKind.Effect => ("Effect Monster", false),
        MonsterKind.NonEffect => ("non-Effect", true),
        MonsterKind.Normal => ("Normal Monster", false),
        MonsterKind.Tuner => ("Tuner", false),
        MonsterKind.NonTuner => ("non-Tuner", true),
        MonsterKind.Fusion => ("Fusion Monster", false),
        MonsterKind.Synchro => ("Synchro Monster", false),
        MonsterKind.Xyz => ("Xyz Monster", false),
        MonsterKind.Link => ("Link Monster", false),
        MonsterKind.Ritual => ("Ritual Monster", false),
        MonsterKind.Pendulum => ("Pendulum Monster", false),
        MonsterKind.Flip => ("Flip monster", false),
        MonsterKind.Toon => ("Toon monster", false),
        MonsterKind.Spirit => ("Spirit monster", false),
        MonsterKind.Union => ("Union monster", false),
        MonsterKind.Gemini => ("Gemini monster", false),
        MonsterKind.Token => ("Token", false),
        MonsterKind.NonToken => ("non-Token", true),
        _ => null,
    };

    /// <summary>"in your other Pendulum Zone": the Pendulum Zone this card isn't in.</summary>
    private static bool OtherPendulumZone(CardFilter f) => f.ExcludeThisCard && f.Zones == Zone.PendulumZone && f.Owner == CardOwner.You;

    /// <summary>Where the cards are: "from your hand or GY", "your opponent controls".</summary>
    public static string LocationText(CardFilter f, string preposition = "from", bool plural = false) => Location(f, preposition, plural);

    private static string Location(CardFilter f, string preposition, bool plural = false)
    {
        if (f.Zones == Zone.None) return "";
        var prep = preposition.Length == 0 ? "" : preposition + " ";
        // "in your other Pendulum Zone", "in your Pendulum Zones", "in a Pendulum Zone".
        if (f.Zones == Zone.PendulumZone)
            return OtherPendulumZone(f) ? $"{prep}your other Pendulum Zone"
                : f.Owner == CardOwner.Either ? (plural ? $"{prep}the Pendulum Zones" : $"{prep}a Pendulum Zone")
                : $"{prep}{(f.Owner == CardOwner.You ? "your" : "your opponent's")} Pendulum Zone{(plural ? "s" : "")}";
        if (f.CenterZone)
        {
            return f.Owner switch
            {
                CardOwner.You => "in your center Main Monster Zone",
                CardOwner.Opponent => "in your opponent's center Main Monster Zone",
                _ => "in a center Main Monster Zone",
            };
        }
        // Javelin Beetle of the Alliance: "1 other card in this card's column".
        if (f.InThisColumn)
        {
            return f.Owner switch
            {
                CardOwner.You => "you control in this card's column",
                CardOwner.Opponent => "your opponent controls in this card's column",
                _ => "in this card's column",
            };
        }
        if (f.PointedToByThisCard)
        {
            return f.Owner switch
            {
                CardOwner.You => "you control that this card points to",
                CardOwner.Opponent => "your opponent controls that this card points to",
                _ => "this card points to",
            };
        }
        if (IsFieldOnly(f))
        {
            // Monsters on the field are always in a Monster Zone, so only Spell & Trap Zones need naming.
            if (f.Zones == Zone.SpellTrapZone && f.CardType == FilterCardType.Monster)
            {
                return f.Owner switch
                {
                    CardOwner.You => "in your Spell & Trap Zone",
                    CardOwner.Opponent => "in your opponent's Spell & Trap Zone",
                    _ => "in a Spell & Trap Zone",
                };
            }
            return f.Owner switch
            {
                CardOwner.You => "you control",
                CardOwner.Opponent => "your opponent controls",
                // Angelechy Destrier: "1 monster in another column": the column says where.
                _ when !f.AnyRequirement && f.Requirements.Any(r => InColumn(r) && r.Op != RequirementOp.IsNot) => "",
                _ => "on the field",
            };
        }

        var owner = f.Owner switch
        {
            CardOwner.You => "your",
            CardOwner.Opponent => "your opponent's",
            _ => "either player's",
        };
        var places = new List<string>();
        if (f.Zones.HasFlag(Zone.Hand)) places.Add("hand");
        if (f.Zones.HasFlag(Zone.Deck)) places.Add("Deck");
        if (f.Zones.HasFlag(Zone.ExtraDeck)) places.Add("Extra Deck");
        if (f.Zones.HasFlag(Zone.Graveyard)) places.Add("GY");
        if (f.Zones.HasFlag(Zone.Banished)) places.Add("banished cards");
        if ((f.Zones & Zone.Field) != Zone.None) places.Add("field");
        else if (f.Zones.HasFlag(Zone.PendulumZone)) places.Add("Pendulum Zone");
        var list = places.Count switch
        {
            1 => places[0],
            2 => $"{places[0]} or {places[1]}",
            _ => string.Join(", ", places.Take(places.Count - 1)) + ", or " + places[^1],
        };
        var prefix = preposition.Length == 0 ? "" : preposition + " ";
        return $"{prefix}{owner} {list}";
    }
}
