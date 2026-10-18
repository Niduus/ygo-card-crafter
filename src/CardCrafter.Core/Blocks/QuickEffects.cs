using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "You can activate the effects of monsters you control with "Ars Magna" in their original names as Quick Effects while
/// you control a "Power Patron" Link Monster" (Ars Magna "Citrinitas"), "... of "Orcust" monsters in your GY ... as Quick
/// Effects" (Orcustrated Babel): this card lets its player activate those monsters' effects as Quick Effects. As official
/// scripts do it, the card is a rule on its player with its own passcode as the code, and it puts its picker in the
/// engine's table of such cards (aux.quick_effect_filter). Only effects written to become Quick Effects while it applies
/// change: "You activate it (Main Phase)" with "a Quick Effect while ... lets you" set to this card's passcode, as the
/// official "Ars Magna", "Orcust" and "Allure Queen" monsters have it.
/// </summary>
public sealed class QuickEffectsPassive : PassiveBlock
{
    public const string BlockId = "passive.quick_effects";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "You can activate the effects of some monsters as Quick Effects";
    public override string Sentence => "You can activate the effects of {cards} as Quick Effects";
    public override string Help => "Like Ars Magna \"Citrinitas\" or Orcustrated Babel. Add IF blocks for while it applies. The monsters' effects that change are " +
                                   "\"You activate it (Main Phase)\" effects whose \"a Quick Effect while ... lets you\" is this card's passcode.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Whose effects", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone | Zone.Graveyard | Zone.Hand),
    ];

    public override string Describe(BlockArgs args) =>
        $"you can activate the effects of {FilterText.Describe(args.Cards("cards"), args.Pack, plural: true, preposition: "in", ownName: args.CardName)} as Quick Effects";

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Args.Cards("cards");
        // The monsters it is about, as the engine asks (aux.IsCanBeQuickEffect): what they are, and where.
        var parts = new List<string>();
        if (f.Zones != Zone.None) parts.Add($"c:IsLocation({Lua.Locations(f.Zones)})");
        var what = ctx.Script.MaterialFilter(f, "quickfilter");
        parts.Add(what == "nil" ? "c:IsType(TYPE_MONSTER)" : $"{what}(c)");
        var filter = ctx.Script.AddFunction("quickeffects", "c", [$"return {string.Join(" and ", parts)}"]);
        var range = RangeOf(ctx);
        ctx.Effect.AddPassive(v =>
        [
            $"aux.quick_effect_filter[id]={filter}",
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            $"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
            $"{v}:SetCode(id)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange(1,0)",
        ]);
    }

    /// <summary>
    /// The ignition effect (<paramref name="effect"/>) becomes a Quick Effect while the card with passcode
    /// <paramref name="code"/> lets its player activate it as one: the ignition effect applies while it doesn't, and a
    /// Quick Effect clone (any time, as official scripts give it) while it does, sharing the once per turn.
    /// </summary>
    public static void BecomesQuick(EffectBuilder effect, long code)
    {
        var granted = $"aux.IsCanBeQuickEffect(e:GetHandler(),tp,{code.ToString(CultureInfo.InvariantCulture)})";
        effect.Conditions.Add("not " + granted);
        effect.Variants.Add(new TriggerVariant
        {
            Types = ["EFFECT_TYPE_QUICK_O"], Code = "EVENT_FREE_CHAIN", Range = effect.Range, HintTiming = "TIMINGS_CHECK_MONSTER+TIMING_END_PHASE",
            Conditions = [granted],
        });
    }
}
