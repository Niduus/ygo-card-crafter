using System.Text.RegularExpressions;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Compiler;

/// <summary>How one effect of a script came out, as the card browser colours it.</summary>
public enum PieceColor
{
    /// <summary>Made into blocks.</summary>
    Green,
    /// <summary>Done another way: a Card tab setting, a summoning rule, or something Card Crafter writes itself.</summary>
    Yellow,
    /// <summary>Not translated, but blocks for it exist: it can probably be built by hand.</summary>
    Orange,
    /// <summary>No block does it.</summary>
    Red,
}

/// <param name="Headline">"Made into blocks", "No block does this", ...</param>
/// <param name="Note">What it became, or why not.</param>
public sealed record PieceVerdict(PieceColor Color, string Headline, string Note);

/// <summary>Sorts a translated piece into green, yellow, orange or red, with a note in plain words.</summary>
public static partial class TranslationVerdict
{
    /// <summary>Effects (EFFECT_...) some block writes. Anything else in a lasting effect has no block.</summary>
    public static readonly HashSet<string> EffectsBlocksWrite =
    [
        "EFFECT_ATTACK_ALL", "EFFECT_AVOID_BATTLE_DAMAGE", "EFFECT_CANNOT_ACTIVATE", "EFFECT_CANNOT_ATTACK", "EFFECT_CANNOT_ATTACK_ANNOUNCE",
        "EFFECT_CANNOT_BE_BATTLE_TARGET", "EFFECT_CANNOT_BE_EFFECT_TARGET", "EFFECT_CANNOT_BE_FUSION_MATERIAL", "EFFECT_CANNOT_BE_LINK_MATERIAL",
        "EFFECT_CANNOT_BE_SYNCHRO_MATERIAL", "EFFECT_CANNOT_BE_XYZ_MATERIAL", "EFFECT_CANNOT_CHANGE_POSITION", "EFFECT_CANNOT_DIRECT_ATTACK",
        "EFFECT_CANNOT_SPECIAL_SUMMON", "EFFECT_CANNOT_TO_HAND", "EFFECT_CHANGE_DAMAGE", "EFFECT_CHANGE_LEVEL", "EFFECT_DESTROY_REPLACE",
        "EFFECT_DIRECT_ATTACK", "EFFECT_DISABLE", "EFFECT_DISABLE_EFFECT", "EFFECT_DISABLE_TRAPMONSTER", "EFFECT_EQUIP_LIMIT", "EFFECT_EXTRA_ATTACK",
        "EFFECT_EXTRA_SUMMON_COUNT", "EFFECT_IMMUNE_EFFECT", "EFFECT_INDESTRUCTABLE_BATTLE", "EFFECT_INDESTRUCTABLE_EFFECT", "EFFECT_MUST_ATTACK",
        "EFFECT_NO_EFFECT_DAMAGE", "EFFECT_PIERCE", "EFFECT_PUBLIC", "EFFECT_QP_ACT_IN_SET_TURN", "EFFECT_SET_ATTACK_FINAL", "EFFECT_SPSUMMON_CONDITION",
        "EFFECT_SPSUMMON_PROC", "EFFECT_TO_GRAVE_REDIRECT", "EFFECT_TRAP_ACT_IN_HAND", "EFFECT_TRAP_ACT_IN_SET_TURN", "EFFECT_UNRELEASABLE_NONSUM",
        "EFFECT_UNRELEASABLE_SUM", "EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE", "EFFECT_UPDATE_LEVEL",
        "EFFECT_CANNOT_BP", "EFFECT_CANNOT_SUMMON", "EFFECT_CANNOT_MSET", "EFFECT_CANNOT_FLIP_SUMMON", "EFFECT_CANNOT_SSET", "EFFECT_CHANGE_CODE",
        "EFFECT_CANNOT_SELECT_BATTLE_TARGET", "EFFECT_DEFENSE_ATTACK", "EFFECT_DOUBLE_TRIBUTE", "EFFECT_EXTRA_ATTACK_MONSTER", "EFFECT_INDESTRUCTABLE_COUNT",
        "EFFECT_LEAVE_FIELD_REDIRECT", "EFFECT_SELF_DESTROY", "EFFECT_SELF_TOGRAVE", "EFFECT_SUMMON_PROC",
    ];

    [GeneratedRegex(@"\(([A-Z+_]*CONTINUOUS[A-Z+_]*) (EVENT_[A-Z_+]+)")]
    private static partial Regex ContinuousHook();

    [GeneratedRegex(@"(EFFECT_[A-Z_]+)")]
    private static partial Regex EffectCode();

    public static PieceVerdict Of(TranslatedPiece t, CardData card)
    {
        if (t.Effect is not null)
            return new(PieceColor.Green, "Made into blocks", string.Join(" ", t.Effects.Select(e => EffectTextGenerator.Describe(e, card))));
        if (t.Procedure is not null)
            return new(PieceColor.Yellow, "Done another way", "Becomes the Summoning conditions on the Card tab: the materials this monster is Summoned with.");
        if (t.Rule is { } rule)
            return new(PieceColor.Yellow, "Done another way", rule == SummonRule.ProcedureOnly
                ? "Becomes the summoning rule on the Card tab: \"Cannot be Normal Summoned/Set. Must be Special Summoned ...\" and only by its own way."
                : "Becomes the summoning rule on the Card tab: \"Cannot be Normal Summoned/Set.\"");
        if (t.NoSpecialSummon)
            return new(PieceColor.Yellow, "Done another way", "Becomes \"Cannot be Special Summoned\" on the Card tab.");
        if (t.EquipTo is not null)
            return new(PieceColor.Yellow, "Done another way", "Becomes the Equip Spell's \"Equip to\" picker on the Card tab, which writes its activation and equip limit.");
        if (t.Setting is not null)
            return new(PieceColor.Yellow, "Done another way", "Becomes a setting on the Card tab, such as \"You can only control 1\" or \"You can only Special Summon once per turn\".");
        if (t.Ignored)
            return new(PieceColor.Yellow, "Done another way", "Needs no block: Card Crafter writes it itself (the card's activation, Revive limit, or the lists of cards it mentions).");

        var reason = t.Reason ?? "it isn't one of the shapes Card Crafter reads";
        if (Red(reason) is { } why) return new(PieceColor.Red, "No block does this", $"{why} Card Crafter keeps it as script ({reason}).");
        return new(PieceColor.Orange, "Not translated yet",
            $"Card Crafter couldn't read this part: {reason}. Blocks for this kind of effect exist, so it can most likely be built by hand from the card text below.");
    }

    /// <summary>Why no block can do it, or null when blocks could.</summary>
    private static string? Red(string reason)
    {
        if (ContinuousHook().Match(reason) is { Success: true } hook)
            return $"It is a hidden rule that runs whenever {hook.Groups[2].Value} happens, which no block writes.";
        if (reason.StartsWith("its lasting effect isn't one the blocks have", StringComparison.Ordinal)
            || reason.StartsWith("it puts an effect on a player the blocks don't have", StringComparison.Ordinal)
            || reason.StartsWith("it gives the target an effect the blocks don't have", StringComparison.Ordinal)
            || reason.StartsWith("it gives this card an effect the blocks don't have", StringComparison.Ordinal))
        {
            var code = EffectCode().Match(reason);
            if (code.Success && !EffectsBlocksWrite.Contains(code.Groups[1].Value)) return $"No block gives {Plain(code.Groups[1].Value)}.";
            if (!code.Success) return "No block gives this kind of lasting effect.";
            return null;
        }
        if (reason.StartsWith("it isn't a single effect created on this card", StringComparison.Ordinal))
            return "It is built by a shared helper or written outside a single effect, which no block writes.";
        if (reason.StartsWith("it does something beside its effects", StringComparison.Ordinal)) return "The script does more than make effects here.";
        if (reason.StartsWith("it keeps extra state", StringComparison.Ordinal)) return "It remembers things between steps in a way no block does.";
        if (reason.StartsWith("it makes an effect it never applies", StringComparison.Ordinal)) return "It makes an effect it never uses.";
        if (reason.StartsWith("it can be used more than once per turn", StringComparison.Ordinal)) return "It can be used twice or more per turn; blocks have once per turn or no limit.";
        if (reason.StartsWith("it looks in LOCATION_PZONE", StringComparison.Ordinal) || reason.Contains("LOCATION_PZONE", StringComparison.Ordinal))
            return "No card picker looks in the Pendulum Zones yet.";
        if (reason.StartsWith("it works from", StringComparison.Ordinal)) return "No WHEN block works from there.";
        return null;
    }

    /// <summary>"EFFECT_CANNOT_SELECT_BATTLE_TARGET" as "cannot select battle target".</summary>
    private static string Plain(string code) => code["EFFECT_".Length..].Replace('_', ' ').ToLowerInvariant() + $" ({code})";
}
