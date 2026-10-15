namespace CardCrafter.Core.Model;

public enum OncePerTurn
{
    None,
    /// <summary>"Once per turn": each copy of the card can use it once.</summary>
    Soft,
    /// <summary>"You can only use this effect of X once per turn": shared by every copy.</summary>
    Hard,
    /// <summary>"You can only use this effect of X once per Duel."</summary>
    Duel,
}

/// <summary>How an action connects to the one before it, matching card-text wording.</summary>
public enum ActionLink
{
    /// <summary>"... and ..." — happens alongside the previous action.</summary>
    And,
    /// <summary>"..., and if you do, ..." — only if the previous action happened, at the same time.</summary>
    IfYouDo,
    /// <summary>"..., then ..." — only if the previous action happened, afterwards.</summary>
    Then,
    /// <summary>"Also, after that, ..." — afterwards, whether or not the previous action happened.</summary>
    AlsoAfterThat,
    /// <summary>"..., and if you don't, ..." — only if the previous action did not happen (or was declined).</summary>
    IfYouDont,
    /// <summary>
    /// "● ..." — starts another option of "Activate 1 of these effects": the player picks one option that can be used,
    /// and only that option's blocks happen.
    /// </summary>
    Option,
}

/// <summary>One placed block: which block it is and the values the user filled in.</summary>
public sealed class BlockInstance
{
    public string BlockId { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = [];
    public Dictionary<string, CardFilter> Filters { get; set; } = [];
    /// <summary>Only meaningful for actions after the first.</summary>
    public ActionLink Link { get; set; } = ActionLink.And;
    /// <summary>A DO block the player may skip: "you can Special Summon this card". They are asked as it resolves.</summary>
    public bool Optional { get; set; }

    public BlockInstance() { }
    public BlockInstance(string blockId) => BlockId = blockId;

    public BlockInstance With(string key, object value)
    {
        Values[key] = value is bool b ? (b ? "true" : "false") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return this;
    }

    public BlockInstance With(string key, CardFilter filter)
    {
        Filters[key] = filter;
        return this;
    }

    public BlockInstance Linked(ActionLink link)
    {
        Link = link;
        return this;
    }

    /// <summary>Makes this DO block optional ("you can ...").</summary>
    public BlockInstance MayDo(bool optional = true)
    {
        Optional = optional;
        return this;
    }
}

/// <summary>
/// One effect on the card, laid out like a row on an event sheet:
/// WHEN (trigger) + IF (conditions) on the left, COST + DO (actions) on the right.
/// </summary>
public sealed class EffectEvent
{
    public string Label { get; set; } = "Effect";
    public BlockInstance? When { get; set; }
    public List<BlockInstance> If { get; set; } = [];
    public List<BlockInstance> Cost { get; set; } = [];
    public List<BlockInstance> Do { get; set; } = [];
    public OncePerTurn Limit { get; set; }
    /// <summary>Effects with the same group number share one hard once-per-turn. 0 means "its own".</summary>
    public int SharedLimitGroup { get; set; }
}
