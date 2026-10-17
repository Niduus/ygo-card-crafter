using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// The Sun God Leading Down into Darkness: "If a monster(s) is destroyed by battle: Its controller takes damage equal to its
/// original ATK": each monster's controller takes damage equal to its stat, both players at once, as the official card
/// adds them up for each player.
/// </summary>
public sealed class ControllerDamageAction : ActionBlock
{
    public const string BlockId = "do.controller_damage";
    public override string Id => BlockId;
    public override string Group => "Life Points";
    public override string Name => "Its controller takes damage equal to its ATK (each monster)";
    public override string Sentence => "Its controller takes damage equal to its {stat} ({which})";
    public override string Help => "For each of the monsters, the player who controls it (or controlled it, if it left the field) takes damage equal to its ATK.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("stat", "Equal to its", new("orig_atk", "original ATK"), new("atk", "ATK"), new("orig_def", "original DEF")),
        ParamSpec.Choice("which", "Which monsters", new("event", "the monster(s) that started this effect"), new("affected", "the card(s) the step before affected"),
            new("target", "this effect's target(s)")),
    ];

    public override string Describe(BlockArgs args) => $"its controller takes damage equal to its {args.Describe("stat")}";

    public override void Compile(BlockContext ctx)
    {
        var group = ctx.Args.Choice("which") switch
        {
            "affected" => CardRefs.GroupLua(ctx, CardRef.Affected),
            "target" => CardRefs.GroupLua(ctx, CardRef.Target),
            _ => "(eg or Group.CreateGroup())",
        };
        if (group is null) return;
        if (ctx.Args.Choice("which") == "event" && ctx.Effect.Code is null or "EVENT_FREE_CHAIN")
            ctx.Effect.Warn($"\"{Name}\": \"the monster(s) that started this effect\" needs a WHEN that is something happening to monsters, like \"a monster is destroyed by battle\".");
        // As it was on the field when it left (destroyed by battle), else as it is now.
        var stat = ctx.Args.Choice("stat") switch
        {
            "atk" => "(tc:IsLocation(LOCATION_MZONE) and tc:GetAttack() or tc:GetPreviousAttackOnField())",
            "orig_def" => "tc:GetBaseDefense()",
            _ => "tc:GetBaseAttack()",
        };
        var fn = ctx.Script.AddFunction("ctrldamage", "g,tp",
        [
            "local d={[0]=0,[1]=0}",
            "for tc in aux.Next(g) do",
            "\tlocal p=tc:IsLocation(LOCATION_MZONE) and tc:GetControler() or tc:GetPreviousControler()",
            $"\td[p]=d[p]+math.max({stat},0)",
            "end",
            "local r=0",
            "if d[tp]>0 then r=r+Duel.Damage(tp,d[tp],REASON_EFFECT,true) end",
            "if d[1-tp]>0 then r=r+Duel.Damage(1-tp,d[1-tp],REASON_EFFECT,true) end",
            "Duel.RDComplete()",
            "return r",
        ]);
        ctx.Effect.AddCategory("CATEGORY_DAMAGE");
        ctx.Effect.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_DAMAGE,nil,0,PLAYER_ALL,0)");
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddStep([$"local {r}={fn}({group},tp)"], r, ctx.Link);
    }
}
