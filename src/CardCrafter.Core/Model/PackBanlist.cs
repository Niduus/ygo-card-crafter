namespace CardCrafter.Core.Model;

/// <summary>
/// A banlist made in Card Crafter: a name and the limit of each card on it (official cards or custom ones).
/// Written into the database's banlists tables, which YGO Omega reads, and as an lflist.conf for ygopro clients.
/// </summary>
public sealed class PackBanlist
{
    /// <summary>Card Crafter's banlists use ids from <see cref="FirstId"/> up, so they never take the id of one of Omega's.</summary>
    public const long FirstId = 1_000_000;

    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Omega's banlist flags, kept as read when a list is copied from the game.</summary>
    public long Flags { get; set; }
    public List<BanlistEntry> Entries { get; set; } = [];
}

public sealed class BanlistEntry
{
    public long Code { get; set; }
    /// <summary>0 Forbidden, 1 Limited, 2 Semi-Limited (3 Unlimited).</summary>
    public int Limit { get; set; }
    /// <summary>The card's name when it was added, so the list reads well without the card database.</summary>
    public string Name { get; set; } = "";
}
