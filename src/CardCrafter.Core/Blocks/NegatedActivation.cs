using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// Cyber Emergency: "If the activation of this card in its owner's possession was negated by your opponent's card effect
/// and sent to your GY: You can ...". A trigger in the GY on the negation (EVENT_CHAIN_NEGATED), for a card negated and
/// destroyed by that effect, and on a custom event raised at the end of the Chain, for a card the rules sent to the GY.
/// The card's activation watches for its own negation to raise that event: <see cref="WatchLines"/> is added to its cost.
/// </summary>
public sealed class NegatedActivationTrigger : BlockDefinition
{
    public const string BlockId = "when.activation_negated";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "This card";
    public override string Name => "This card's activation is negated by the opponent and it goes to the GY";
    public override string Sentence => "If the activation of this card in its owner's possession was negated by your opponent's card effect and sent to your GY";
    public override string Help => "Cyber Emergency's GY effect, for Spell and Trap Cards. The card's activation is written so it notices the negation.";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Flag("optional", "optional (\"You can\")", true)];

    public override string Describe(BlockArgs args) => Sentence;

    /// <summary>True when the card has this trigger, so its activation must watch for the negation.</summary>
    public static bool Watches(CardProject project) => project.Effects.Any(e => e.When?.BlockId == BlockId);

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (ctx.Card.Kind == CardKind.Monster) e.Error($"\"{Name}\" is for Spell and Trap Cards: only they are activated as cards.");
        if (!ReferenceEquals(ctx.Args.Instance, e.Source.When) || e.Source.AlsoWhen.Count > 0)
        {
            e.Error($"\"{Name}\" starts its effect on its own: it can't be combined with \"Or when\".");
            return;
        }
        var optional = ctx.Args.Bool("optional");
        e.Types.Add("EFFECT_TYPE_FIELD");
        e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
        e.IsMandatory = !optional;
        e.Code = "EVENT_CHAIN_NEGATED";
        e.AddProperty("EFFECT_FLAG_DELAY");
        e.Range = "LOCATION_GRAVE";
        // Negated and destroyed (or sent) by the opponent's effect: it is already in the GY when the negation happens.
        var negated = ctx.Script.AddFunction("negcon", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local de,dp=Duel.GetChainInfo(ev,CHAININFO_DISABLE_REASON,CHAININFO_DISABLE_PLAYER)",
            "return rp==tp and de and dp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE)",
            "\tand e:GetHandler()==re:GetHandler() and e:GetHandler():GetReasonEffect()==de",
        ]);
        e.Conditions.Add($"{negated}(e,tp,eg,ep,ev,re,r,rp)");
        // Only negated: the rules send it to the GY as the Chain resolves, and its activation raises the event at the Chain's end.
        e.Variants.Add(new TriggerVariant
        {
            Types = e.Types.ToList(), Code = "EVENT_CUSTOM+id", Range = e.Range, Properties = ["EFFECT_FLAG_DELAY"],
            Conditions = ["e:GetHandler()==re:GetHandler() and e:GetHandler():GetReasonEffect()==nil"],
        });
        e.TriggerConditionCount = e.Conditions.Count;
    }

    /// <summary>
    /// The lines that go at the end of the card's activation cost, as Cyber Emergency's negreg: while the card is being
    /// activated, a watcher for this Chain raises EVENT_CUSTOM+id at the end of the Chain if the opponent negated it.
    /// </summary>
    public static IEnumerable<string> WatchLines(ScriptBuilder script)
    {
        var raise = script.AddFunction("negevent", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local te=e:GetLabelObject()",
            "Duel.RaiseEvent(te:GetHandler(),EVENT_CUSTOM+id,te,0,tp,tp,0)",
            "e:Reset()",
        ]);
        var check = script.AddFunction("negcheck", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local te=e:GetLabelObject()",
            "local de,dp=Duel.GetChainInfo(ev,CHAININFO_DISABLE_REASON,CHAININFO_DISABLE_PLAYER)",
            "if rp==tp and de and dp==1-tp and re==te then",
            "\tlocal e1=Effect.CreateEffect(e:GetHandler())",
            "\te1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "\te1:SetCode(EVENT_CHAIN_END)",
            $"\te1:SetOperation({raise})",
            "\te1:SetLabelObject(te)",
            "\tDuel.RegisterEffect(e1,tp)",
            "end",
        ]);
        return
        [
            "if not e:IsHasType(EFFECT_TYPE_ACTIVATE) then return end",
            "local c=e:GetHandler()",
            "local e1=Effect.CreateEffect(c)",
            "e1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "e1:SetCode(EVENT_CHAIN_NEGATED)",
            "e1:SetLabelObject(e)",
            $"e1:SetOperation({check})",
            "e1:SetReset(RESET_CHAIN)",
            "Duel.RegisterEffect(e1,tp)",
        ];
    }

    /// <summary>
    /// Adds the watcher to the card's activations (an effect's own, or the plain one Card Crafter writes); an error when
    /// the card has none that can carry it.
    /// </summary>
    public static void Watch(ScriptBuilder script, IReadOnlyList<EffectBuilder> built, bool plainActivation)
    {
        var activations = built.Where(b => b.IsActivation).ToList();
        foreach (var a in activations) a.CostLines.AddRange(WatchLines(script));
        if (activations.Count == 0 && !plainActivation)
            script.Diagnostics.Add(new(Severity.Error, "\"If the activation of this card ... was negated\" needs the card to be activated: give it an effect with \"Activate this Spell/Trap\"."));
    }
}
