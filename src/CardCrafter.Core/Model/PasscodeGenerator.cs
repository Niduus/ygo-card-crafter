namespace CardCrafter.Core.Model;

/// <summary>
/// Picks passcodes for new cards so players never have to. Each card gets its own block of 10,000 numbers
/// (passcodes ending in 0000), because the engine also uses passcode+100, +200... internally for
/// "once per turn" limits; a whole free block keeps those from clashing with other cards too.
/// Passcodes stay at or under <see cref="MaxForStrings"/>, so the card's effect texts work in the game.
/// </summary>
public static class PasscodeGenerator
{
    public const long BlockSize = 10_000;
    public const long First = 100_000_000;
    public const long Last = 134_210_000;

    /// <summary>
    /// The largest passcode whose effect texts the game can find. aux.Stringid(id,n) is id*16+n, and YGO Omega reads
    /// it as a signed 32-bit number, so it must stay under 2^31: a larger passcode points nowhere, and prompts show
    /// the card's location ("hand") or "Unknown (-1968087296)" instead of the effect.
    /// </summary>
    public const long MaxForStrings = 134_217_727;

    public static long Generate(IEnumerable<long> used, Random? random = null)
    {
        random ??= Random.Shared;
        var taken = new SortedSet<long>(used);
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            var id = random.NextInt64(First / BlockSize, Last / BlockSize + 1) * BlockSize;
            if (taken.GetViewBetween(id, id + BlockSize - 1).Count == 0) return id;
        }
        // Practically unreachable: 90,000 blocks. Fall back to the first free single number.
        var next = First;
        while (taken.Contains(next)) next++;
        return next;
    }

    /// <summary>True when two cards are close enough that their once-per-turn codes could collide.</summary>
    public static bool TooClose(long a, long b) => a != b && Math.Abs(a - b) < 1_600;
}
