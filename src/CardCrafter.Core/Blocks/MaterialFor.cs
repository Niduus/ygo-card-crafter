using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "as material for the Ritual Summon of a LIGHT Reptile monster": which monster this card was material for, as the
/// official cards say it (Marincess Springirl, K9-66a Jokul, Triggering Wurm). The engine keeps that monster as this
/// card's reason card.
/// </summary>
public static class MaterialFor
{
    public static ParamSpec Picker() =>
        ParamSpec.Cards("summoner", "The monster Summoned with it (any)", () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster });

    /// <summary>Whether the picker says more than "a monster".</summary>
    public static bool Picks(CardFilter summoner) =>
        FilterCompiler.Conditions(summoner).Any(c => c != "c:IsType(TYPE_MONSTER)") || summoner.CardCode != 0;

    /// <summary>"a Ritual Summon", or "the Ritual Summon of a LIGHT Reptile monster" when the picker narrows it.</summary>
    public static string Summon(string kindLabel, CardFilter summoner, CardPack? pack)
    {
        var summon = kindLabel.Length == 0 ? "Summon" : kindLabel + " Summon";
        if (!Picks(summoner)) return "a " + summon;
        var noun = FilterText.Describe(summoner, pack, false, "", withLocation: false);
        return $"the {summon} of {(!noun.StartsWith('"') && "AEIOUaeiou".Contains(noun[0]) ? "an " : "a ")}{noun}";
    }

    /// <summary>The check on the monster this card was material for, or null when any monster will do.</summary>
    public static string? Test(BlockContext ctx, string card)
    {
        var summoner = ctx.Args.Cards("summoner").Clone();
        summoner.Zones = Zone.None;
        return Picks(summoner) ? $"{ctx.Script.Filter(summoner, null, "summonerfilter").Function}({card}:GetReasonCard(),e,tp)" : null;
    }
}

/// <summary>
/// IF: this card was used as material for a Summon, optionally of a picked monster. For effects that start on
/// something else, such as "If this card is sent to the GY", but only count when it went there as material.
/// </summary>
public sealed class UsedAsMaterialCondition : ConditionBlock
{
    public const string BlockId = "if.used_as_material";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card was used as material (for which monster)";
    public override string Sentence => "this card was used as material for {kind} {summoner}";
    public override string Help => "Pick the monster Summoned with it, like \"a LIGHT Reptile monster\" or an archetype. Fusion, Synchro, Link and Ritual materials go to the GY.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("kind", "Summon",
            new("any", "", "any Summon"), new("fusion", "Fusion"), new("synchro", "Synchro"), new("xyz", "Xyz"),
            new("link", "Link"), new("ritual", "Ritual")),
        MaterialFor.Picker(),
    ];

    public override string Describe(BlockArgs args) =>
        $"this card was used as material for {MaterialFor.Summon(args.Choice("kind") == "any" ? "" : args.Describe("kind"), args.Cards("summoner"), args.Pack)}";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var reason = ctx.Args.Choice("kind") switch
        {
            "fusion" => "REASON_FUSION",
            "synchro" => "REASON_SYNCHRO",
            "xyz" => "REASON_XYZ",
            "link" => "REASON_LINK",
            "ritual" => "REASON_RITUAL",
            _ => "REASON_MATERIAL",
        };
        var test = $"e:GetHandler():IsReason({reason})";
        if (MaterialFor.Test(ctx, "e:GetHandler()") is { } of) test += " and " + of;
        ctx.Effect.Conditions.Add(test);
    }
}
