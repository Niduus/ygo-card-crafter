using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>
/// Writes suggested card text from the event sheet, following official wording patterns:
/// "[Once per turn, ][trigger/conditions]: You can [cost], then target [x]; [action], and if you do, [action]."
/// It is a starting point the user can always edit by hand.
/// </summary>
public static partial class EffectTextGenerator
{
    public static string Generate(CardProject project, CardPack? pack = null) =>
        string.Join("\n", project.Effects.Select(e => Describe(e, project.Card, pack)).Where(s => s.Length > 0));

    public static string Describe(EffectEvent effect, CardData card, CardPack? pack = null)
    {
        if (effect.When is null || BlockRegistry.Find(effect.When.BlockId) is not { } trigger) return "";

        bool passive = trigger is WhileFaceUpTrigger;
        bool activation = trigger is ActivateTrigger;
        bool optional = trigger is not (FlipTrigger or ActivateTrigger or WhileFaceUpTrigger)
                        && !(effect.When.Values.TryGetValue("optional", out var o) && string.Equals(o, "false", StringComparison.OrdinalIgnoreCase));

        var head = Head(trigger, effect, pack, passive);
        if (effect.Limit == OncePerTurn.Soft && !passive)
            head = head.Length == 0 ? "Once per turn" : "Once per turn, " + SentenceCase.Lower(head);

        var (targetClause, actions) = Actions(effect.Do, pack);
        string text;
        if (passive)
        {
            text = head.Length == 0 ? SentenceCase.Upper(actions) : $"{SentenceCase.Upper(head)}, {actions}";
        }
        else
        {
            var costs = string.Join(" and ", effect.Cost.Select(b => Text(b, pack)).Where(t => t.Length > 0));
            var before = targetClause is null ? costs : costs.Length == 0 ? targetClause : $"{costs}, then {targetClause}";
            var body = before.Length == 0 ? actions : $"{before}; {actions}";
            body = optional ? "You can " + body : SentenceCase.Upper(body);
            text = head.Length == 0 ? body : $"{SentenceCase.Upper(head)}: {body}";
        }
        text = text.TrimEnd() + ".";

        if (effect.Limit == OncePerTurn.Hard && !passive)
            text += activation
                ? $" You can only activate 1 \"{card.Name}\" per turn."
                : $" You can only use this effect of \"{card.Name}\" once per turn.";
        return text;
    }

    /// <summary>The part before the colon: trigger wording plus IF conditions.</summary>
    private static string Head(BlockDefinition trigger, EffectEvent effect, CardPack? pack, bool passive)
    {
        var when = Text(effect.When!, pack);
        var conditions = effect.If.Select(b => Text(b, pack)).Where(t => t.Length > 0).ToList();
        var cond = string.Join(" and ", conditions);

        string head;
        if (cond.Length == 0) head = when;
        else if (when.Length == 0) head = Clause(cond, passive ? "while" : "if");
        else head = $"{when} {Clause(cond, "while")}";

        if (trigger is QuickTrigger) head = head.Length == 0 ? "(Quick Effect)" : $"{head} (Quick Effect)";
        return head;
    }

    /// <summary>"during your turn" stays as is; anything else gets a joining word such as "if" or "while".</summary>
    private static string Clause(string cond, string joiner) =>
        cond.StartsWith("during ", StringComparison.Ordinal) ? cond : $"{joiner} {cond}";

    private static (string? Target, string Text) Actions(List<BlockInstance> blocks, CardPack? pack)
    {
        string? target = null;
        var sb = new System.Text.StringBuilder();
        foreach (var b in blocks)
        {
            if (BlockRegistry.Find(b.BlockId) is not { } def) continue;
            var args = new BlockArgs(def, b, pack);
            string t;
            if (target is null && def.DescribeTargeting(args) is { } targeted)
            {
                target = Clean(targeted.Target);
                t = Clean(targeted.Action);
            }
            else t = Text(b, pack);
            if (t.Length == 0) continue;
            if (sb.Length > 0)
            {
                sb.Append(b.Link switch
                {
                    ActionLink.IfYouDo => ", and if you do, ",
                    ActionLink.Then => ", then ",
                    ActionLink.AlsoAfterThat => ". Also, after that, ",
                    _ => ", and ",
                });
            }
            sb.Append(t);
        }
        return (target, sb.ToString());
    }

    private static string Text(BlockInstance b, CardPack? pack)
    {
        var def = BlockRegistry.Find(b.BlockId);
        return def is null ? "" : SentenceCase.Lower(Clean(def.Describe(new BlockArgs(def, b, pack))));
    }

    /// <summary>Collapses the double spaces left by empty blanks.</summary>
    private static string Clean(string s) => Spaces().Replace(s, " ").Replace(" ,", ",").Trim();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}

/// <summary>Capitalisation that never lowercases card terms like "Dragon", "GY" or "Special Summon".</summary>
public static class SentenceCase
{
    private static readonly HashSet<string> OrdinaryWords = new(StringComparer.Ordinal)
    {
        "Add", "Activate", "Banish", "Destroy", "Discard", "Draw", "During", "Gain", "Gains", "If", "Inflict", "It",
        "Pay", "Return", "Send", "Shuffle", "There", "This", "Tribute", "While", "You", "Your", "When", "Once",
    };

    /// <summary>Lowercases the first letter only when the first word is an ordinary word.</summary>
    public static string Lower(string s)
    {
        if (s.Length == 0) return s;
        var space = s.IndexOf(' ');
        var first = space < 0 ? s : s[..space];
        return OrdinaryWords.Contains(first) ? char.ToLowerInvariant(s[0]) + s[1..] : s;
    }

    public static string Upper(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
