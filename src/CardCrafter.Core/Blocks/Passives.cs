using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

public abstract class PassiveBlock : BlockDefinition
{
    public override BlockKind Kind => BlockKind.Passive;

    /// <summary>Lines for a lasting effect that applies to this card only.</summary>
    protected static IEnumerable<string> SelfEffect(string v, string range, string code, string value) =>
    [
        $"{v}:SetType(EFFECT_TYPE_SINGLE)",
        $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)",
        $"{v}:SetRange({range})",
        $"{v}:SetCode({code})",
        $"{v}:SetValue({value})",
    ];

    protected static string RangeOf(BlockContext ctx) => ctx.Effect.Range ?? "LOCATION_MZONE";

    /// <summary>
    /// "{cards} ..." lasting effects whose picker names this card itself: one effect on this card per code, instead of
    /// an effect over the matching monsters. True when it was written.
    /// </summary>
    protected static bool OnThisCard(BlockContext ctx, IEnumerable<string> codes, string value)
    {
        if (!ctx.Args.Cards("cards").ThisCard) return false;
        var range = RangeOf(ctx);
        foreach (var code in codes) ctx.Effect.AddPassive(v => SelfEffect(v, range, code, value));
        return true;
    }
}

public sealed class BoostCardsPassive : PassiveBlock
{
    public override string Id => "passive.boost_cards";
    public override string Group => "Monsters";
    public override string Name => "Monsters gain or lose ATK/DEF";
    public override string Sentence => "{cards} {dir} {amount} {stat}";
    public override string Help => "For example: Dragon monsters you control gain 500 ATK.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone, thisCard: true),
        ParamSpec.Choice("dir", "Gain/lose", new("gain", "gain"), new("lose", "lose")),
        ParamSpec.Int("amount", "Amount", 500, 1, 99999),
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF")),
    ];

    public override string Describe(BlockArgs args)
    {
        var cards = args.Cards("cards");
        if (cards.ThisCard) return $"this card {args.Describe("dir")}s {args.Int("amount")} {args.Describe("stat")}";
        var noun = Text.FilterText.Describe(cards, args.Pack, plural: true, preposition: "");
        return $"{noun} {args.Describe("dir")} {args.Int("amount")} {args.Describe("stat")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var self = ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "lose" ? -1 : 1);
        if (OnThisCard(ctx, ctx.Args.Choice("stat") switch { "def" => ["EFFECT_UPDATE_DEFENSE"], "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"], _ => new[] { "EFFECT_UPDATE_ATTACK" } },
                self.ToString(System.Globalization.CultureInfo.InvariantCulture))) return;
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("boosttg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        var amount = ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "lose" ? -1 : 1);
        var codes = ctx.Args.Choice("stat") switch
        {
            "def" => new[] { "EFFECT_UPDATE_DEFENSE" },
            "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
            _ => ["EFFECT_UPDATE_ATTACK"],
        };
        var range = RangeOf(ctx);
        foreach (var code in codes)
        {
            ctx.Effect.AddPassive(v =>
            [
                $"{v}:SetType(EFFECT_TYPE_FIELD)",
                $"{v}:SetCode({code})",
                $"{v}:SetRange({range})",
                $"{v}:SetTargetRange({f.Self},{f.Opp})",
                $"{v}:SetTarget({target})",
                $"{v}:SetValue({amount})",
            ]);
        }
    }
}

public sealed class SelfStatsPassive : PassiveBlock
{
    public override string Id => "passive.self_stats";
    public override string Group => "This card";
    public override string Name => "This card gains or loses ATK/DEF";
    public override string Sentence => "This card {dir} {amount} {stat}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("dir", "Gains/loses", new("gain", "gains"), new("lose", "loses")),
        ParamSpec.Int("amount", "Amount", 500, 1, 99999),
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF")),
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var amount = (ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "lose" ? -1 : 1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var codes = ctx.Args.Choice("stat") switch
        {
            "def" => new[] { "EFFECT_UPDATE_DEFENSE" },
            "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
            _ => ["EFFECT_UPDATE_ATTACK"],
        };
        var range = RangeOf(ctx);
        foreach (var code in codes)
            ctx.Effect.AddPassive(v => SelfEffect(v, range, code, amount));
    }
}

public sealed class IndestructiblePassive : PassiveBlock
{
    public override string Id => "passive.indestructible";
    public override string Group => "This card";
    public override string Name => "This card can't be destroyed";
    public override string Sentence => "This card cannot be destroyed {by} {with}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("by", "By",
            new("battle", "by battle"),
            new("effect", "by card effects"),
            new("both", "by battle or card effects"),
            new("opponent_effect", "by your opponent's card effects"),
            new("spelltrap_effect", "by Spell/Trap effects"),
            new("monster_effect", "by monster effects"),
            new("battle_with", "by battle with", "by battle with some monsters (picked below)")),
        ParamSpec.Cards("with", "By battle with", () => new CardFilter { CardType = FilterCardType.Monster, Summoned = FilterSummoned.FromExtra }, Zone.MonsterZone)
            .ShowIf(b => b.Values.GetValueOrDefault("by") == "battle_with"),
    ];

    public override string Describe(BlockArgs args)
    {
        if (args.Choice("by") != "battle_with") return $"this card cannot be destroyed {args.Describe("by")}";
        var noun = Text.FilterText.Describe(args.Cards("with"), args.Pack, withLocation: false, ownName: args.CardName);
        return $"this card cannot be destroyed by battle with {DoubleTributePassive.Article(noun)}";
    }

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var by = ctx.Args.Choice("by");
        if (by == "battle_with")
        {
            // The value gets the monster this card battles.
            Zones.RequireMonster(ctx, "Cannot be destroyed by battle");
            var with = ctx.Args.Cards("with").Clone();
            with.Zones = Zone.None;
            with.Owner = CardOwner.Either;
            var tests = FilterCompiler.Conditions(with).Where(c => c != "c:IsType(TYPE_MONSTER)").DefaultIfEmpty("true");
            var fn = ctx.Script.AddFunction("indbval", "e,c", [$"return {string.Join(" and ", tests)}"]);
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_INDESTRUCTABLE_BATTLE", fn));
            return;
        }
        if (by is "spelltrap_effect" or "monster_effect")
        {
            var test = by == "monster_effect" ? "re:IsActiveType(TYPE_MONSTER)" : "re:IsActiveType(TYPE_SPELL+TYPE_TRAP)";
            var fn = ctx.Script.AddFunction("indval", "e,re,rp", [$"return {test}"]);
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_INDESTRUCTABLE_EFFECT", fn));
            return;
        }
        if (by is "battle" or "both")
        {
            Zones.RequireMonster(ctx, "Cannot be destroyed by battle");
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_INDESTRUCTABLE_BATTLE", "1"));
        }
        if (by is "effect" or "both")
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_INDESTRUCTABLE_EFFECT", "1"));
        if (by == "opponent_effect")
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_INDESTRUCTABLE_EFFECT", "aux.indoval"));
    }
}

public sealed class UntargetablePassive : PassiveBlock
{
    public override string Id => "passive.untargetable";
    public override string Group => "This card";
    public override string Name => "This card can't be targeted by card effects";
    public override string Sentence => "{who}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "By",
            new("opponent", "Your opponent cannot target this card with card effects", "by your opponent's card effects"),
            new("any", "This card cannot be targeted by card effects", "by any card effects (Dragoon)")),
    ];

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_CANNOT_BE_EFFECT_TARGET", ctx.Args.Choice("who") == "any" ? "1" : "aux.tgoval"));
    }
}

public sealed class BattleAbilityPassive : PassiveBlock
{
    public override string Id => "passive.battle_ability";
    public override string Group => "This card";
    public override string Name => "Battle ability (direct attack, piercing, extra attack)";
    public override string Sentence => "This card {ability}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("ability", "Ability",
            new("direct", "can attack directly"),
            new("pierce", "inflicts piercing battle damage"),
            new("extra", "can make a second attack during each Battle Phase"),
            new("extra2", "can make up to 3 attacks during each Battle Phase"),
            new("extra_monster", "can make up to 2 attacks on monsters during each Battle Phase"),
            new("extra_monster2", "can make up to 3 attacks on monsters during each Battle Phase"),
            // Juggernaut Liebe: "During each Battle Phase, this card can make attacks on monsters up to the number of materials it has +1."
            new("extra_monster_materials", "can make attacks on monsters up to the number of materials it has +1"),
            new("defense_attack", "can attack while in face-up Defense Position. If it does, apply its DEF for damage calculation"),
            new("defense_attack_atk", "can attack while in face-up Defense Position. If it does, apply its ATK for damage calculation")),
    ];

    public override string Describe(BlockArgs args) => args.Choice("ability") == "extra_monster_materials"
        ? "during each Battle Phase, this card " + args.Describe("ability")
        : base.Describe(args);

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var range = RangeOf(ctx);
        var (code, value) = ctx.Args.Choice("ability") switch
        {
            "pierce" => ("EFFECT_PIERCE", "1"),
            "extra" => ("EFFECT_EXTRA_ATTACK", "1"),
            "extra2" => ("EFFECT_EXTRA_ATTACK", "2"),
            "extra_monster" => ("EFFECT_EXTRA_ATTACK_MONSTER", "1"),
            "extra_monster2" => ("EFFECT_EXTRA_ATTACK_MONSTER", "2"),
            "extra_monster_materials" => ("EFFECT_EXTRA_ATTACK_MONSTER", ctx.Script.AddFunction("extraatkval", "e,c", ["return e:GetHandler():GetOverlayCount()"])),
            // A value of 1 makes the damage calculation use its DEF; 0 keeps its ATK.
            "defense_attack" => ("EFFECT_DEFENSE_ATTACK", "1"),
            "defense_attack_atk" => ("EFFECT_DEFENSE_ATTACK", "0"),
            _ => ("EFFECT_DIRECT_ATTACK", "1"),
        };
        ctx.Effect.AddPassive(v => SelfEffect(v, range, code, value));
    }
}

/// <summary>"Your opponent cannot target other monsters you control for attacks" (Marshmallon Glasses, Command Knight).</summary>
public sealed class AttackTargetsPassive : PassiveBlock
{
    public const string BlockId = "passive.attack_targets";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Your opponent cannot target some of your monsters for attacks";
    public override string Sentence => "Your opponent cannot target {cards} for attacks";
    public override string Help => "Tick \"other\" for \"other monsters you control\", so attacks must go at this card.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true }, Zone.MonsterZone, thisCard: true),
    ];

    public override string Describe(BlockArgs args)
    {
        var cards = args.Cards("cards").Clone();
        cards.Owner = CardOwner.You;
        if (cards.ThisCard) return "your opponent cannot target this card for attacks";
        return $"your opponent cannot target {Text.FilterText.Describe(cards, args.Pack, plural: true, preposition: "")} for attacks";
    }

    public override void Compile(BlockContext ctx)
    {
        // This card itself: it can't be chosen as an attack target by the opponent.
        if (OnThisCard(ctx, ["EFFECT_CANNOT_BE_BATTLE_TARGET"], "aux.imval1")) return;
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Owner = CardOwner.You;
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var value = ctx.Script.AddFunction("atklimit", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        var range = RangeOf(ctx);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            $"{v}:SetCode(EFFECT_CANNOT_SELECT_BATTLE_TARGET)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange(0,LOCATION_MZONE)",
            $"{v}:SetValue({value})",
        ]);
    }
}

/// <summary>
/// "Once per turn, if this card would be destroyed by battle, it is not destroyed" (Scarred Warrior), and for other
/// monsters "The first time each "Raise Moon" monster you control would be destroyed by card effect each turn, it is not destroyed".
/// </summary>
public sealed class NotDestroyedOncePassive : PassiveBlock
{
    public const string BlockId = "passive.not_destroyed_once";
    public override string Id => BlockId;
    public override string Group => "Protection";
    public override string Name => "Once per turn, it is not destroyed";
    public override string Sentence => "Once per turn, if {cards} would be destroyed {by}, it is not destroyed";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec
        {
            Key = "cards", Label = "Which monsters", Type = ParamType.Cards, AllowThisCard = true, AllowedZones = Zone.MonsterZone,
            DefaultFilter = () => new CardFilter { ThisCard = true, Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster },
        },
        ParamSpec.Choice("by", "By",
            new("battle", "by battle"),
            new("effect", "by card effect"),
            new("both", "by battle or card effect"),
            new("opponent_effect", "by an opponent's card effect")),
    ];

    public override string Describe(BlockArgs args)
    {
        var cards = args.Cards("cards");
        if (cards.ThisCard) return $"once per turn, if this card would be destroyed {args.Describe("by")}, it is not destroyed";
        var noun = Text.FilterText.Describe(cards, args.Pack, plural: false, preposition: "");
        return $"the first time each {noun} would be destroyed {args.Describe("by")} each turn, it is not destroyed";
    }

    public static string Check(string by) => by switch
    {
        "effect" => "r&REASON_EFFECT~=0",
        "both" => "r&(REASON_BATTLE+REASON_EFFECT)~=0",
        "opponent_effect" => "r&REASON_EFFECT~=0 and rp==1-e:GetHandlerPlayer()",
        _ => "r&REASON_BATTLE~=0",
    };

    public override void Compile(BlockContext ctx)
    {
        var cards = ctx.Args.Cards("cards");
        var check = Check(ctx.Args.Choice("by"));
        var range = RangeOf(ctx);
        if (cards.ThisCard)
        {
            Zones.RequireMonster(ctx, Name);
            var value = ctx.Script.AddFunction("indct", "e,re,r,rp", [$"return {check}"]);
            ctx.Effect.AddPassive(v =>
            [
                $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)",
                $"{v}:SetRange({range})",
                $"{v}:SetCode(EFFECT_INDESTRUCTABLE_COUNT)",
                $"{v}:SetCountLimit(1)",
                $"{v}:SetValue({value})",
            ]);
            return;
        }
        var filter = cards.Clone();
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("indtg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        var fieldValue = ctx.Script.AddFunction("indct", "e,re,r,rp", [$"if {check} then return 1 else return 0 end"]);
        var (self, opp) = filter.Owner switch { CardOwner.Opponent => ("0", "LOCATION_MZONE"), CardOwner.Either => ("LOCATION_MZONE", "LOCATION_MZONE"), _ => ("LOCATION_MZONE", "0") };
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            $"{v}:SetCode(EFFECT_INDESTRUCTABLE_COUNT)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange({self},{opp})",
            $"{v}:SetTarget({target})",
            $"{v}:SetValue({fieldValue})",
        ]);
    }
}

/// <summary>
/// "You can Normal Summon this card without Tributing" (Cyber Dragon Core's cousins, Photon Thrasher...), with the
/// effect's IF blocks as when it is allowed: "If your opponent controls a monster and you control no monsters, ...".
/// </summary>
public sealed class NoTributeSummonPassive : PassiveBlock
{
    public const string BlockId = "passive.no_tribute_summon";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "You can Normal Summon this card without Tributing";
    public override string Sentence => "You can Normal Summon this card without Tributing";
    public override string Help => "For Level 5 or higher monsters. Add IF blocks for when it is allowed, like \"your opponent controls a monster\" and \"you control no monsters\".";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        if (ctx.Card.Level < 5) ctx.Effect.Warn("\"Without Tributing\" matters for Level 5 or higher monsters; lower ones need no Tribute anyway.");
        // The Summon procedure is checked from the hand with (e,c,minc): its IF blocks go in its own condition.
        var extra = ctx.Effect.Conditions.ToList();
        ctx.Effect.Conditions.Clear();
        var check = string.Join(" and ", new[] { "minc==0", "c:IsLevelAbove(5)", "Duel.GetLocationCount(tp,LOCATION_MZONE)>0" }.Concat(extra));
        var con = ctx.Script.AddFunction("ntcon", "e,c,minc", ["if c==nil then return true end", "local tp=c:GetControler()", $"return {check}"]);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetDescription(aux.Stringid(id,{Math.Min(ctx.Effect.Index, 16) - 1}))",
            $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)",
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}:SetCode(EFFECT_SUMMON_PROC)",
            $"{v}:SetCondition({con})",
        ]);
    }
}

/// <summary>"This card's name becomes "Dark Magician" while on the field or in the GY."</summary>
public sealed class NameBecomesPassive : PassiveBlock
{
    public const string BlockId = "passive.name_becomes";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card's name becomes another card's name";
    public override string Sentence => "This card's name becomes {card} while {where} {on_field}";
    public override string Help => "Pick the card by name. Effects that look for that name count this card too.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec { Key = "card", Label = "Which card's name", Type = ParamType.Cards, WhereFromBlock = true, DefaultFilter = () => new CardFilter { CardType = FilterCardType.Any, CardCode = 46986414 } },
        ParamSpec.Choice("where", "While",
            new("field", "on the field"),
            new("field_gy", "on the field or in the GY"),
            new("gy", "in the GY"),
            new("hand_field", "in the hand or on the field"),
            new("field_zone", "in the Field Zone"),
            TreatedAsPlaces.Option),
        .. TreatedAsPlaces.Params(),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("card");
        var name = f.CardCode == 0 ? "(pick a card)" : Text.CardNames.Find(f.CardCode, args.Pack) is { } n ? $"\"{n}\"" : $"the name of passcode {f.CardCode}";
        var where = args.Choice("where") == TreatedAsPlaces.Choice ? TreatedAsPlaces.Text(args) : args.Describe("where");
        return $"this card's name becomes {name} while {where}";
    }

    public override void Compile(BlockContext ctx)
    {
        var code = ctx.Args.Cards("card").CardCode;
        if (code == 0) { ctx.Effect.Error($"\"{Name}\": pick the card whose name this card takes."); return; }
        var field = ctx.Card.IsMonster ? "LOCATION_MZONE" : "LOCATION_SZONE";
        var loc = ctx.Args.Choice("where") switch
        {
            "field_gy" => field + "+LOCATION_GRAVE",
            "gy" => "LOCATION_GRAVE",
            "hand_field" => "LOCATION_HAND+" + field,
            "field_zone" => "LOCATION_FZONE",
            TreatedAsPlaces.Choice => TreatedAsPlaces.Range(ctx.Args, ctx.Card),
            _ => field,
        };
        if (loc is null) { ctx.Effect.Error($"\"{Name}\": tick at least one place where this card's name changes."); return; }
        // As aux.EnableChangeCode does: the card also counts as mentioning that name.
        ctx.Script.AddInitialLine($"aux.AddCodeList(c,{Lua.Num(code)})");
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)",
            $"{v}:SetCode(EFFECT_CHANGE_CODE)",
            $"{v}:SetRange({loc})",
            $"{v}:SetValue({Lua.Num(code)})",
        ]);
    }
}

/// <summary>"Cannot be used as Synchro Material, except for the Synchro Summon of a WATER monster."</summary>
public sealed class MaterialBanPassive : PassiveBlock
{
    public const string BlockId = "passive.material_ban";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card cannot be used as Fusion/Synchro/Xyz/Link Material";
    public override string Sentence => "This card cannot be used as {kind} Material, except for {cards}";
    public override string Help => "Leave the exception as plain monsters for none.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("kind", "Material for", new("synchro", "Synchro"), new("xyz", "Xyz"), new("link", "Link"), new("fusion", "Fusion")),
        ParamSpec.Cards("cards", "Except for the Summon of (plain monsters for none)", () => new CardFilter { CardType = FilterCardType.Monster }, Zone.MonsterZone),
    ];

    private static bool HasExcept(CardFilter f) => FilterCompiler.Conditions(f).Any(c => c != "c:IsType(TYPE_MONSTER)");

    public override string Describe(BlockArgs args)
    {
        var kind = args.Describe("kind");
        var f = args.Cards("cards");
        if (!HasExcept(f)) return $"this card cannot be used as {kind} Material";
        var noun = Text.FilterText.Describe(f, args.Pack, withLocation: false);
        return $"this card cannot be used as {kind} Material, except for the {kind} Summon of {(noun.StartsWith('"') ? "" : "AEIOUaeiou".Contains(noun[0]) ? "an " : "a ")}{noun}";
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var code = ctx.Args.Choice("kind") switch
        {
            "xyz" => "EFFECT_CANNOT_BE_XYZ_MATERIAL",
            "link" => "EFFECT_CANNOT_BE_LINK_MATERIAL",
            "fusion" => "EFFECT_CANNOT_BE_FUSION_MATERIAL",
            _ => "EFFECT_CANNOT_BE_SYNCHRO_MATERIAL",
        };
        var f = ctx.Args.Cards("cards");
        var value = "1";
        if (HasExcept(f))
        {
            var mf = ctx.Script.MaterialFilter(f, "matlimit");
            value = ctx.Script.AddFunction("matval", "e,c", ["if not c then return false end", $"return not {mf}(c)"]);
        }
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)",
            $"{v}:SetCode({code})",
            $"{v}:SetValue({value})",
        ]);
    }
}

/// <summary>"If you control no other "Gravekeeper's" monsters, destroy this card": a lasting rule with IF blocks for when.</summary>
public sealed class SelfDestroyPassive : PassiveBlock
{
    public const string BlockId = "passive.self_destroy";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Destroy this card / send it to the GY (when its IF blocks hold)";
    public override string Sentence => "{how} this card";
    public override string Help => "Add IF blocks for when, like \"you control no other Dragon monsters\". It isn't an effect that activates.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "What", new("destroy", "Destroy"), new("grave", "Send to the GY:")),
    ];

    public override string Describe(BlockArgs args) => args.Choice("how") == "grave" ? "send this card to the GY" : "destroy this card";

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Effect.Conditions.Count == 0) ctx.Effect.Warn($"\"{Describe(ctx.Args)}\" with no IF blocks happens as soon as it is on the field.");
        var range = RangeOf(ctx);
        var code = ctx.Args.Choice("how") == "grave" ? "EFFECT_SELF_TOGRAVE" : "EFFECT_SELF_DESTROY";
        // The engine only checks "send itself to the GY" effects once a card has asked it to.
        if (code == "EFFECT_SELF_TOGRAVE") ctx.Script.AddInitialLine("Duel.EnableGlobalFlag(GLOBALFLAG_SELF_TOGRAVE)");
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)",
            $"{v}:SetRange({range})",
            $"{v}:SetCode({code})",
        ]);
    }
}

/// <summary>"You can Normal Summon 1 Fiend monster in addition to your Normal Summon/Set": a lasting rule while this card is face-up.</summary>
public sealed class ExtraSummonPassive : PassiveBlock
{
    public const string BlockId = "passive.extra_summon";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "You can Normal Summon an extra monster (while face-up)";
    public override string Sentence => "You can Normal Summon 1 {cards} in addition to your Normal Summon/Set";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster }, Zone.Hand),
    ];

    public override string Describe(BlockArgs args) =>
        $"you can Normal Summon 1 {Text.FilterText.Describe(args.Cards("cards"), args.Pack, withLocation: false)} in addition to your Normal Summon/Set";

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Zones = Zone.Hand;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("exstg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())"]);
        var range = RangeOf(ctx);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            $"{v}:SetCode(EFFECT_EXTRA_SUMMON_COUNT)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange(LOCATION_HAND+LOCATION_MZONE,0)",
            $"{v}:SetTarget({target})",
        ]);
    }
}
