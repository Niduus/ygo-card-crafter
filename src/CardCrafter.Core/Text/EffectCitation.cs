using System.Text.RegularExpressions;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>
/// Finds the sentence of a card's text that each effect of its script stands for, so the card browser can quote it.
/// Effects and sentences come in the same order; each effect gets the sentence that shares the most words with what it
/// does (its blocks' text, or the words its script uses).
/// </summary>
public static partial class EffectCitation
{
    /// <summary>The card text's effect sentences, without "You can only use each effect ... once per turn".</summary>
    public static List<string> Sentences(string text)
    {
        var result = new List<string>();
        foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var parts = Split(line);
            // "● Add a card from the Deck to the hand.": a bullet belongs to the sentence that lists it.
            if (line.TrimStart().StartsWith('●') && result.Count > 0 && parts.Count > 0)
            {
                result[^1] += " " + parts[0];
                parts.RemoveAt(0);
            }
            foreach (var p in parts)
            {
                if (p.Length < 8 || p.StartsWith("[", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(p, @"^You can only (use|activate) (each|this|1)\b") && !p.Contains(':') && !p.Contains(';')) continue;
                result.Add(p);
            }
        }
        return result;
    }

    /// <summary>A line's sentences: a period ends one, unless it is inside a "quoted name".</summary>
    private static List<string> Split(string line)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            sb.Append(ch);
            if (ch == '"') quoted = !quoted;
            if (!quoted && ch == '.' && (i + 1 == line.Length || line[i + 1] == ' '))
            {
                if (sb.ToString().Trim() is { Length: > 0 } t) parts.Add(t);
                sb.Clear();
            }
        }
        if (sb.ToString().Trim() is { Length: > 0 } rest) parts.Add(rest);
        return parts;
    }

    private static readonly HashSet<string> Stop =
    [
        "the", "this", "that", "and", "you", "your", "can", "card", "cards", "from", "its", "it", "with", "for", "per", "turn", "once", "only",
        "effect", "effects", "their", "them", "they", "then", "also", "any", "all", "use", "has", "have", "are", "was", "been", "into", "except",
    ];

    [GeneratedRegex("[A-Za-z][A-Za-z-]+")]
    private static partial Regex Word();

    private static HashSet<string> Words(string s) =>
        Word().Matches(s).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 3 && !Stop.Contains(w)).Select(Stem).ToHashSet();

    private static string Stem(string w) => w.EndsWith("ies", StringComparison.Ordinal) ? w[..^3] + "y"
        : w.EndsWith("ed", StringComparison.Ordinal) && w.Length > 5 ? w[..^2]
        : w.EndsWith('s') && w.Length > 4 ? w[..^1] : w;

    /// <summary>Words a script's names stand for: "Duel.Draw" is "draw", "EVENT_REMOVE" is "banished".</summary>
    private static readonly (string Lua, string Words)[] Hints =
    [
        ("Duel.Draw", "draw"), ("CATEGORY_DRAW", "draw"), ("SendtoHand", "add hand return"), ("CATEGORY_SEARCH", "add deck hand"), ("CATEGORY_TOHAND", "add hand return"),
        ("Duel.Destroy", "destroy"), ("CATEGORY_DESTROY", "destroy"), ("Duel.Remove", "banish"), ("CATEGORY_REMOVE", "banish"), ("SpecialSummon", "special summon"),
        ("CATEGORY_SPECIAL_SUMMON", "special summon"), ("NegateActivation", "negate activation"), ("NegateEffect", "negate"), ("CATEGORY_NEGATE", "negate"),
        ("CATEGORY_DISABLE", "negate"), ("Duel.Damage", "inflict damage"), ("CATEGORY_DAMAGE", "inflict damage"), ("Duel.Recover", "gain"), ("CATEGORY_RECOVER", "gain"),
        ("SendtoGrave", "send"), ("CATEGORY_TOGRAVE", "send"), ("SendtoDeck", "deck shuffle return"), ("CATEGORY_TODECK", "deck shuffle return"),
        ("Duel.Release", "tribute"), ("CATEGORY_RELEASE", "tribute"), ("DiscardHand", "discard"), ("CATEGORY_HANDES", "discard"), ("ChangePosition", "position change"),
        ("CATEGORY_POSITION", "position change"), ("GetControl", "control"), ("CATEGORY_CONTROL", "control"), ("Duel.Overlay", "attach material"),
        ("RemoveOverlayCard", "detach material"), ("Duel.Equip", "equip"), ("CATEGORY_EQUIP", "equip"), ("DiscardDeck", "send top deck"), ("CATEGORY_DECKDES", "send top deck"),
        ("EFFECT_UPDATE_ATTACK", "gain lose atk"), ("EFFECT_UPDATE_DEFENSE", "gain lose def"), ("CATEGORY_ATKCHANGE", "atk"), ("EFFECT_SET_ATTACK", "atk become"),
        ("INDESTRUCTABLE", "destroyed cannot"), ("EFFECT_IMMUNE_EFFECT", "unaffected"), ("CANNOT_BE_EFFECT_TARGET", "cannot target"),
        ("CANNOT_SELECT_BATTLE_TARGET", "cannot target attack"), ("CANNOT_BE_BATTLE_TARGET", "cannot target attack"), ("EFFECT_CANNOT_ATTACK", "cannot attack"),
        ("EFFECT_PIERCE", "piercing battle damage defense"), ("EFFECT_SPSUMMON_CONDITION", "special summon must"), ("EFFECT_SUMMON_PROC", "normal summon without tribute"),
        ("EVENT_SUMMON_SUCCESS", "normal summon"), ("EVENT_SPSUMMON_SUCCESS", "special summon"), ("EVENT_FLIP", "flip"), ("EVENT_TO_GRAVE", "sent"),
        ("EVENT_DESTROYED", "destroyed"), ("EVENT_BATTLE_DESTROYED", "destroyed battle"), ("EVENT_REMOVE", "banished"), ("EVENT_RELEASE", "tributed"),
        ("EVENT_TO_HAND", "hand added"), ("EVENT_LEAVE_FIELD", "leaves field"), ("PHASE_END", "end phase"), ("PHASE_STANDBY", "standby phase"),
        ("EVENT_ATTACK_ANNOUNCE", "attack declares"), ("EVENT_BATTLE_DESTROYING", "destroys battle"), ("EVENT_CHAINING", "activates activated"),
        ("EVENT_BE_MATERIAL", "material"), ("EVENT_BATTLE_DAMAGE", "inflicts battle damage"), ("EFFECT_TYPE_QUICK_O", "quick"), ("EFFECT_TYPE_ACTIVATE", "activate"),
        ("LOCATION_GRAVE", "gy"), ("LOCATION_HAND", "hand"), ("LOCATION_DECK", "deck"), ("LOCATION_REMOVED", "banished"), ("LOCATION_EXTRA", "extra"),
        ("RACE_SPELLCASTER", "spellcaster"), ("RACE_DRAGON", "dragon"), ("RACE_WARRIOR", "warrior"), ("RACE_REPTILE", "reptile"), ("TYPE_XYZ", "xyz"),
        ("TYPE_SYNCHRO", "synchro"), ("TYPE_FUSION", "fusion"), ("TYPE_LINK", "link"), ("TYPE_RITUAL", "ritual"), ("TYPE_TUNER", "tuner"), ("TYPE_TRAP", "trap"),
        ("TYPE_SPELL", "spell"), ("EFFECT_COUNT_CODE_DUEL", "duel"), ("SelectYesNo", "can"),
    ];

    private static HashSet<string> WordsOf(TranslatedPiece t, CardData card)
    {
        var words = new HashSet<string>();
        if (t.Effect is not null) words.UnionWith(Words(string.Join(" ", t.Effects.Select(e => EffectTextGenerator.Describe(e, card)))));
        var code = t.Piece.Preview;
        foreach (var (lua, w) in Hints)
            if (code.Contains(lua, StringComparison.Ordinal)) words.UnionWith(Words(w));
        foreach (Match m in Regex.Matches(code, @"\bLevel\((\d+)\)|IsLevel\((\d+)\)")) words.Add("level");
        words.UnionWith(Words(t.Piece.Title));
        return words;
    }

    /// <summary>The card-text sentence each piece stands for, or null for pieces that match none (like "Activate").</summary>
    public static string?[] Assign(string cardText, IReadOnlyList<TranslatedPiece> pieces, CardData card)
    {
        var sentences = Sentences(cardText);
        var result = new string?[pieces.Count];
        if (sentences.Count == 0 || pieces.Count == 0) return result;
        var sWords = sentences.Select(Words).ToList();
        var score = new double[pieces.Count, sentences.Count];
        for (int i = 0; i < pieces.Count; i++)
        {
            // Pieces that need no block (the card's activation, Revive limit) quote nothing.
            if (pieces[i].Ignored) continue;
            var pw = WordsOf(pieces[i], card);
            for (int j = 0; j < sentences.Count; j++)
            {
                var shared = sWords[j].Count(pw.Contains);
                score[i, j] = shared == 0 ? 0 : shared / Math.Sqrt(Math.Max(1, sWords[j].Count));
            }
        }
        // The best matches first, each sentence quoted at most once; nearer positions break ties, since card text
        // and script usually list effects in the same order.
        var pairs = new List<(double Score, int Piece, int Sentence)>();
        for (int i = 0; i < pieces.Count; i++)
            for (int j = 0; j < sentences.Count; j++)
                if (score[i, j] > 0)
                    pairs.Add((score[i, j] + 0.25 * (1 - Math.Abs((double)i / pieces.Count - (double)j / sentences.Count)), i, j));
        var usedSentences = new HashSet<int>();
        var quoted = new List<int>?[pieces.Count];
        foreach (var (_, i, j) in pairs.OrderByDescending(p => p.Score))
        {
            if (quoted[i] is not null || usedSentences.Contains(j)) continue;
            quoted[i] = [j];
            usedSentences.Add(j);
        }
        // A sentence left over that one effect clearly does too ("If you activated this card from your hand, ...").
        for (int j = 0; j < sentences.Count; j++)
        {
            if (usedSentences.Contains(j)) continue;
            var bestPiece = Enumerable.Range(0, pieces.Count).Where(i => quoted[i] is not null).OrderByDescending(i => score[i, j]).FirstOrDefault(-1);
            if (bestPiece >= 0 && score[bestPiece, j] >= 1.2) { quoted[bestPiece]!.Add(j); usedSentences.Add(j); }
        }
        for (int i = 0; i < pieces.Count; i++)
            if (quoted[i] is { } js) result[i] = string.Join(" ", js.OrderBy(j => j).Select(j => sentences[j]));
        return result;
    }
}
