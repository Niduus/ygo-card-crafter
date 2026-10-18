using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Every property of <see cref="CardProperties.All"/> with every comparison it offers, in a real duel: a tester in the GY
/// has an ignition effect "if (a card matching the requirement) exists: gain 100 LP", the field holds cards that should
/// or shouldn't match, and the duel shows whether it could be activated. Board states the harness can't make with its
/// own commands (an ATK change, an equip, a counter, a control switch, a Ritual or Pendulum Summon...) are set up at the
/// first Standby Phase by a scripted "rigger" card, also in the GY.
/// </summary>
public static class RequirementMatrix
{
    public const long Tester = 100130001, S = 100130002, Other = 100130003, Rigger = 100130009;
    public const long XyzId = 100130010, LinkId = 100130011, PendId = 100130012, LinkLeft = 100130013, LinkRight = 100130014;
    public const long SummonXyz = 100130020, SummonLink = 100130021, SummonSynchro = 100130022, SummonFusion = 100130023, Poly = 100130024;
    public const long TunerId = 100130025, RitualId = 100130026, TributeId = 100130027, ZeroId = 100130028, Silencer = 100130029, Attacker = 100130030;
    public const int Arch = 0x7a1, OtherArch = 0x7a2;
    public const long MentionsId = 100049301;

    public static CardRequirement R(string property, RequirementOp op, int value = 0, int value2 = 0, params long[] values) =>
        new() { Property = property, Op = op, Value = value, Value2 = value2, Values = [.. values] };

    public static CardRequirement Is(string property, params long[] values) => R(property, RequirementOp.Is, 0, 0, values);
    public static CardRequirement IsNot(string property, params long[] values) => R(property, RequirementOp.IsNot, 0, 0, values);

    public static CardFilter Monsters(CardOwner owner = CardOwner.You, MonsterKind kind = MonsterKind.Any) =>
        new() { Owner = owner, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = kind };

    /// <summary>Any card on the field: monsters, Spells and Traps, face-up or face-down.</summary>
    public static CardFilter AnyCard() => new() { Owner = CardOwner.You, Zones = Zone.Field, CardType = FilterCardType.Any };

    public static BlockInstance Exists(CardFilter f)
    {
        var b = new BlockInstance("if.cards_exist").With("count", 1);
        b.Filters["cards"] = f;
        return b;
    }

    public static CardProject TesterCard(BlockInstance condition) => new()
    {
        Card = new CardData { Id = Tester, Name = "Matrix Tester", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0, Attribute = CardAttribute.Earth },
        Effects = [new EffectEvent { When = new BlockInstance("when.ignition").With("zone", "graveyard"), If = [condition], Do = [new BlockInstance("do.gain_lp").With("amount", 100)] }],
    };

    public static CardProject Mon(long id, string name = "", int atk = 2000, int def = 1200, int level = 4, CardAttribute attr = CardAttribute.Dark,
        CardRace race = CardRace.Fiend, MonsterFrame frame = MonsterFrame.Normal, MonsterAbility abilities = MonsterAbility.None,
        LinkMarker markers = LinkMarker.None, int scale = 0, int[]? archetypes = null) => new()
    {
        Card = new CardData
        {
            Id = id, Name = name.Length > 0 ? name : $"Matrix {id}", Frame = frame, Abilities = abilities, Level = level, Atk = atk, Def = def,
            Attribute = attr, Race = race, LinkMarkers = markers, LeftScale = scale, RightScale = scale, Archetypes = [.. archetypes ?? []],
        },
    };

    /// <summary>A Spell in the GY whose script runs <paramref name="body"/> once, at the first Standby Phase (tp is player 0).</summary>
    public static CardProject RiggerCard(string body) => new()
    {
        Card = new CardData { Id = Rigger, Name = "Matrix Rigger", Kind = CardKind.Spell },
        UseCustomLua = true,
        CustomLua = $$"""
            local s,id=GetID()
            function s.initial_effect(c)
            	local e1=Effect.CreateEffect(c)
            	e1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)
            	e1:SetCode(EVENT_PHASE+PHASE_STANDBY)
            	e1:SetRange(LOCATION_GRAVE)
            	e1:SetCountLimit(1)
            	e1:SetOperation(s.op)
            	c:RegisterEffect(e1)
            end
            local function find(code,loc,p)
            	return Duel.GetFirstMatchingCard(Card.IsCode,p,loc,0,nil,code)
            end
            local function single(c,code,value)
            	local e1=Effect.CreateEffect(c)
            	e1:SetType(EFFECT_TYPE_SINGLE)
            	e1:SetCode(code)
            	if value then e1:SetValue(value) end
            	e1:SetReset(RESET_EVENT+RESETS_STANDARD)
            	c:RegisterEffect(e1)
            end
            function s.op(e,tp,eg,ep,ev,re,r,rp)
            {{body}}
            end
            """,
    };

    /// <summary>One board to test requirements on.</summary>
    /// <param name="Player">Who owns the tester (and so who "you" is).</param>
    /// <param name="Others">Activations other than the tester's that always happen (a Spell that sets the board up).</param>
    public sealed record Setup(
        CardProject[] Cards, Action<EngineScenario> Place, Func<CardFilter> Filter,
        int Player = 0, int Others = 0, string? Rig = null, string[]? Checks = null, int Turns = 1);

    private static Setup Simple(Func<CardFilter> filter, params CardProject[] onField) =>
        new(onField, s => { foreach (var c in onField) s.Card(c.Card.Id, 0, "mzone"); }, filter);

    private static Setup Rigged(string rig, Func<CardFilter> filter, CardProject[] cards, Action<EngineScenario> place, string[]? checks = null) =>
        new(cards, place, filter, Rig: rig, Checks: checks);

    private static CardProject Subject() => Mon(S, "Subject", archetypes: [Arch]);

    private static CardProject Xyz(long id = XyzId) => Mon(id, "Matrix Xyz", 2200, 1800, 4, frame: MonsterFrame.Xyz);

    private static CardProject Link(long id, LinkMarker markers) => Mon(id, $"Matrix Link {id}", 1600, 0, 0, frame: MonsterFrame.Link, markers: markers);

    private static CardProject ProcXyz()
    {
        var p = Mon(SummonXyz, "Summoned Xyz", 2200, 1800, 4, frame: MonsterFrame.Xyz);
        p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 };
        return p;
    }

    private static CardProject ProcLink()
    {
        var p = Link(SummonLink, LinkMarker.Bottom | LinkMarker.BottomRight);
        p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2, MaterialFilter = new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Effect } };
        return p;
    }

    private static CardProject ProcSynchro()
    {
        var p = Mon(SummonSynchro, "Summoned Synchro", 2400, 1000, 6, frame: MonsterFrame.Synchro);
        p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1 };
        return p;
    }

    private static CardProject MentioningFusion(long id, MaterialSlot[] slots)
    {
        var p = Mon(id, "Mentioning Fusion", 2400, 1000, 6, frame: MonsterFrame.Fusion);
        p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, Materials = [.. slots] };
        return p;
    }

    private static CardProject ProcFusion()
    {
        var p = Mon(SummonFusion, "Summoned Fusion", 2400, 1000, 6, frame: MonsterFrame.Fusion);
        p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Warrior, Warrior] };
        return p;
    }

    private static CardProject PolyCard() => new()
    {
        Card = new CardData { Id = Poly, Name = "Matrix Fusion", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Do = [new BlockInstance(FusionSummonAction.BlockId).With("mats", "hand_field")] }],
    };

    private static CardProject SilencerCard() => new()
    {
        Card = new CardData { Id = Silencer, Name = "Matrix Silencer", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Do = [new BlockInstance("do.negate_effects").With("target", true)] }],
    };

    private static string SpSummon(long code, string loc, string type = "0", string zone = "") =>
        $"\tDuel.SpecialSummon(find({code},{loc},tp),{type},tp,tp,true,false,POS_FACEUP_ATTACK{(zone.Length > 0 ? "," + zone : "")})";

    private static string Single(long code, string effect, string? value = null, string loc = "LOCATION_MZONE", string p = "tp") =>
        $"\tsingle(find({code},{loc},{p}),{effect}{(value is null ? "" : "," + value)})";

    // The battle boards are player 1's: it attacks in turn 2 and activates the tester in Main Phase 2 (the harness goes
    // to Main Phase 2 while a monster is still to be Special Summoned, so a code that is never offered keeps it going).
    private static Setup Battle(bool target, Func<CardFilter> filter) => new(
        [Mon(Attacker, "Matrix Attacker", 2000, 1000)],
        s =>
        {
            s.Card(Attacker, 1, "mzone");
            if (target) s.Card(Warrior, 0, "mzone");
            s.Attack(Attacker).SpSummon(1).Turns(2);
        },
        filter, Player: 1, Turns: 2,
        // Nothing is destroyed by the battle, so both monsters stay to be looked at.
        Rig: "\tfor tc in aux.Next(Duel.GetMatchingGroup(nil,tp,LOCATION_MZONE,LOCATION_MZONE,nil)) do single(tc,EFFECT_INDESTRUCTABLE_BATTLE,1) end");

    public static readonly Dictionary<string, Setup> Setups = new()
    {
        // A Normal Monster: 2000 ATK, 1200 DEF, Level 4, DARK Fiend, archetype Arch, placed face-up in Attack Position.
        ["stat"] = Simple(() => Monsters(), Subject()),
        ["stat_any"] = Simple(AnyCard, Subject()),
        ["zero_any"] = Simple(AnyCard, Mon(ZeroId, "Zero", 0, 0)),
        ["effect"] = new([], s => s.Card(EffectFiller, 0, "mzone"), () => Monsters()),
        ["tuner"] = Simple(() => Monsters(), Mon(TunerId, "Matrix Tuner", 500, 500, 2, abilities: MonsterAbility.Tuner)),
        ["xyz"] = Simple(() => Monsters(), Xyz()),
        ["link"] = Simple(() => Monsters(), Link(LinkId, LinkMarker.Bottom | LinkMarker.BottomRight)),
        ["pendulum"] = Simple(() => Monsters(), Mon(PendId, "Matrix Pendulum", 1500, 1500, 4, abilities: MonsterAbility.Pendulum, scale: 3)),
        ["spell_any"] = new([], s => s.Card(Spell, 0, "szone"), AnyCard),
        ["trap_any"] = new([], s => s.Card(Trap, 0, "szone"), AnyCard),
        // A Pendulum Monster in the Pendulum Zone is a Spell there.
        ["pzone_any"] = Rigged("\tDuel.MoveToField(find(" + PendId + ",LOCATION_HAND,tp),tp,tp,LOCATION_PZONE,POS_FACEUP,true)", AnyCard,
            [Mon(PendId, "Matrix Pendulum", 1500, 1500, 4, abilities: MonsterAbility.Pendulum, scale: 3)], s => s.Card(PendId, 0, "hand"),
            [$"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_PZONE,0,1,nil,{PendId})"]),
        // +500 ATK, +500 DEF, +1 Level; now LIGHT and a Warrior; in Defense Position.
        ["boosted"] = Rigged(string.Join("\n", Single(S, "EFFECT_UPDATE_ATTACK", "500"), Single(S, "EFFECT_UPDATE_DEFENSE", "500"), Single(S, "EFFECT_UPDATE_LEVEL", "1"),
                Single(S, "EFFECT_CHANGE_ATTRIBUTE", "ATTRIBUTE_LIGHT"), Single(S, "EFFECT_CHANGE_RACE", "RACE_WARRIOR")),
            () => Monsters(), [Subject()], s => s.Card(S, 0, "mzone"),
            [$"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{S}):GetAttack()==2500"]),
        ["defense"] = Rigged($"\tDuel.ChangePosition(find({S},LOCATION_MZONE,tp),POS_FACEUP_DEFENSE)", () => Monsters(), [Subject()], s => s.Card(S, 0, "mzone")),
        // Summons.
        ["normal_summoned"] = new([Subject()], s => s.Card(S, 0, "hand").Summon(S), () => Monsters()),
        // The harness can't answer the Tribute prompt of a Tribute Summon: the Summon record is written as a Tribute Summon leaves it.
        ["tribute_summoned"] = Rigged($"\tDebug.PreSummon(find({TributeId},LOCATION_MZONE,tp),SUMMON_TYPE_ADVANCE,LOCATION_HAND)", () => Monsters(),
            [Mon(TributeId, "Matrix Level 5", 2300, 1000, 5)], s => s.Card(TributeId, 0, "mzone")),
        ["sp_from_deck"] = Rigged(SpSummon(S, "LOCATION_DECK"), () => Monsters(), [Subject()], s => s.Card(S, 0, "deck")),
        ["sp_from_grave"] = Rigged(SpSummon(S, "LOCATION_GRAVE"), () => Monsters(), [Subject()], s => s.Card(S, 0, "grave")),
        ["sp_from_banished"] = Rigged(SpSummon(S, "LOCATION_REMOVED"), () => Monsters(), [Subject()], s => s.Card(S, 0, "removed")),
        ["xyz_summoned"] = new([ProcXyz()], s => s.Card(Warrior, 0, "mzone", 2).Card(SummonXyz, 0, "extra").SpSummon(SummonXyz), () => Monsters(kind: MonsterKind.Xyz),
            Checks: [$"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{SummonXyz}):GetOverlayCount()==2"]),
        ["link_summoned"] = new([ProcLink()], s => s.Card(EffectFiller, 0, "mzone", 2).Card(SummonLink, 0, "extra").SpSummon(SummonLink), () => Monsters(kind: MonsterKind.Link),
            Checks: [$"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{SummonLink}):GetSequence()==5"]),
        ["synchro_summoned"] = new([ProcSynchro(), Mon(TunerId, "Matrix Tuner", 500, 500, 2, abilities: MonsterAbility.Tuner)],
            s => s.Card(TunerId, 0, "mzone").Card(Warrior, 0, "mzone").Card(SummonSynchro, 0, "extra").SpSummon(SummonSynchro), () => Monsters(kind: MonsterKind.Synchro),
            Checks: [$"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{SummonSynchro})"]),
        // A Fusion Monster whose materials name the archetype ("1 "Ember" monster + 1 monster"), and one whose don't.
        ["fusion_mentions"] = Simple(() => Monsters(kind: MonsterKind.Fusion), MentioningFusion(MentionsId, [new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Archetype = Arch } }, new MaterialSlot()])),
        ["fusion_plain"] = Simple(() => Monsters(kind: MonsterKind.Fusion), MentioningFusion(MentionsId, [new MaterialSlot(), new MaterialSlot()])),
        ["fusion_summoned"] = new([ProcFusion(), PolyCard()],
            s => s.Card(Poly, 0, "hand").Card(Warrior, 0, "hand", 2).Card(SummonFusion, 0, "extra").Activate(Poly), () => Monsters(kind: MonsterKind.Fusion), Others: 1,
            Checks: [$"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{SummonFusion})"]),
        ["ritual_summoned"] = Rigged(SpSummon(RitualId, "LOCATION_HAND", "SUMMON_TYPE_RITUAL"), () => Monsters(),
            [Mon(RitualId, "Matrix Ritual", 2500, 2000, 6, frame: MonsterFrame.Ritual)], s => s.Card(RitualId, 0, "hand")),
        ["pendulum_summoned"] = Rigged(SpSummon(PendId, "LOCATION_HAND", "SUMMON_TYPE_PENDULUM"), () => Monsters(),
            [Mon(PendId, "Matrix Pendulum", 1500, 1500, 4, abilities: MonsterAbility.Pendulum, scale: 3)], s => s.Card(PendId, 0, "hand")),
        // The harness can't Flip Summon: the Summon record is written as a Flip Summon of a Set monster leaves it.
        ["flip_summoned"] = Rigged($"\tDebug.PreSummon(find({S},LOCATION_MZONE,tp),SUMMON_TYPE_NORMAL+SUMMON_TYPE_FLIP,LOCATION_HAND)", () => Monsters(), [Subject()], s => s.Card(S, 0, "mzone")),
        // A Link-1 Special Summoned to the left Extra Monster Zone, pointing (or not) at the subject in the leftmost Main Monster Zone.
        ["pointed_at"] = Rigged(SpSummon(LinkLeft, "LOCATION_EXTRA", "SUMMON_TYPE_LINK", "0x20"), () => Monsters(), [Subject(), Link(LinkLeft, LinkMarker.BottomLeft)],
            s => s.Card(S, 0, "mzone").Card(LinkLeft, 0, "extra"),
            [$"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{LinkLeft}):GetLinkedGroupCount()==1"]),
        ["pointed_at_normal"] = Rigged(SpSummon(LinkLeft, "LOCATION_EXTRA", "SUMMON_TYPE_LINK", "0x20"), () => Monsters(kind: MonsterKind.Normal), [Subject(), Link(LinkLeft, LinkMarker.BottomLeft)],
            s => s.Card(S, 0, "mzone").Card(LinkLeft, 0, "extra")),
        ["pointed_away_normal"] = Rigged(SpSummon(LinkRight, "LOCATION_EXTRA", "SUMMON_TYPE_LINK", "0x20"), () => Monsters(kind: MonsterKind.Normal), [Subject(), Link(LinkRight, LinkMarker.BottomRight)],
            s => s.Card(S, 0, "mzone").Card(LinkRight, 0, "extra")),
        // Equip Cards, counters, control, targeting, negation.
        ["equipped1"] = Rigged(Equip(1), () => Monsters(), [Subject()], s => s.Card(S, 0, "mzone").Card(Spell, 0, "hand", 2)),
        ["equipped2"] = Rigged(Equip(2), () => Monsters(), [Subject()], s => s.Card(S, 0, "mzone").Card(Spell, 0, "hand", 2),
            [$"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{S}):GetEquipCount()==2"]),
        // 3 counters of one kind: GetCounter(0) only says 1 (kinds of counters), which is why "counters" is a yes/no.
        ["counters"] = Rigged($"\tfind({S},LOCATION_MZONE,tp):AddCounter(0x1001,3)", () => Monsters(), [Subject()], s => s.Card(S, 0, "mzone"),
            [$"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{S}):GetCounter(0x1001)==3",
             $"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{S}):GetCounter(0)==1"]),
        ["control"] = Rigged($"\tDuel.GetControl(find({S},LOCATION_MZONE,1-tp),tp)", () => Monsters(), [Subject()], s => s.Card(S, 1, "mzone"),
            [$"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{S}):GetOwner()==1"]),
        ["untargetable"] = Rigged(Single(S, "EFFECT_CANNOT_BE_EFFECT_TARGET", "1"), () => Monsters(), [Subject()], s => s.Card(S, 0, "mzone")),
        ["opp_effect"] = new([], s => s.Card(EffectFiller, 1, "mzone"), () => Monsters(CardOwner.Opponent)),
        ["negated_by_spell"] = new([SilencerCard()], s => s.Card(EffectFiller, 1, "mzone").Card(Silencer, 0, "hand").Activate(Silencer), () => Monsters(CardOwner.Opponent), Others: 1),
        ["negated_rig"] = Rigged(string.Join("\n", Single(EffectFiller, "EFFECT_DISABLE", null, p: "1-tp"), Single(EffectFiller, "EFFECT_DISABLE_EFFECT", null, p: "1-tp")),
            () => Monsters(CardOwner.Opponent), [], s => s.Card(EffectFiller, 1, "mzone")),
        // Battles (player 1's turn 2): a direct attack, an attack on a monster, no attack.
        ["direct_attack"] = Battle(false, () => Monsters()),
        ["battle"] = Battle(true, () => Monsters()),
        ["battle_attacked"] = Battle(true, () => Monsters(CardOwner.Opponent)),
        ["no_attack"] = new([Mon(Attacker, "Matrix Attacker", 2000, 1000)], s => s.Card(Attacker, 1, "mzone").SpSummon(1).Turns(2), () => Monsters(), Player: 1, Turns: 2),
        // The subject in the GY, looked for there (the tester is in the GY too, so the filter names the subject).
        ["in_grave"] = new([Subject()], s => s.Card(S, 0, "grave"), () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardCode = S }),
    };

    private static string Equip(int n) => $$"""
        	local tc=find({{S}},LOCATION_MZONE,tp)
        	local g=Duel.GetMatchingGroup(Card.IsCode,tp,LOCATION_HAND,0,nil,{{Spell}})
        	local i=0
        	for ec in aux.Next(g) do
        		if i<{{n}} and Duel.Equip(tp,ec,tc,true,true) then single(ec,EFFECT_EQUIP_LIMIT,aux.TRUE) i=i+1 end
        	end
        	Duel.EquipComplete()
        """;

    /// <summary>Builds the duel for one board and one set of requirements and checks the tester could (or couldn't) be activated.</summary>
    public static void Run(string setup, bool activates, params CardRequirement[] reqs)
    {
        var s = Setups[setup];
        var filter = s.Filter();
        filter.Requirements = [.. reqs];
        var cards = new List<CardProject> { TesterCard(Exists(filter)) };
        cards.AddRange(s.Cards);
        if (s.Rig is not null) cards.Add(RiggerCard(s.Rig));
        var scenario = new EngineScenario([.. cards]).Card(Tester, s.Player, "grave");
        if (s.Rig is not null) scenario.Card(Rigger, 0, "grave");
        s.Place(scenario);
        scenario.Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(Tester)
            .ExpectActivations((activates ? 1 : 0) + s.Others)
            .Check($"Duel.GetLP({s.Player})=={(activates ? 8100 : 8000)}");
        foreach (var c in s.Checks ?? []) scenario.Check(c);
        var (code, log) = scenario.Run();
        Assert.True(code == 0, $"{setup} / {string.Join(", ", reqs.Select(r => $"{r.Property} {r.Op} {r.Value} {r.Value2} [{string.Join(",", r.Values)}]"))} should {(activates ? "" : "not ")}match\n{log}");
    }

    /// <summary>Each comparison at, just above and just below the value <paramref name="v"/> the board's card has.</summary>
    public static IEnumerable<(RequirementOp Op, int A, int B, bool Match)> Around(int v) =>
    [
        (RequirementOp.GreaterOrEqual, v, 0, true), (RequirementOp.GreaterOrEqual, v + 1, 0, false),
        (RequirementOp.LessOrEqual, v, 0, true), (RequirementOp.LessOrEqual, v - 1, 0, false),
        (RequirementOp.Equal, v, 0, true), (RequirementOp.Equal, v + 1, 0, false), (RequirementOp.Equal, v - 1, 0, false),
        (RequirementOp.NotEqual, v, 0, false), (RequirementOp.NotEqual, v + 1, 0, true),
        (RequirementOp.Greater, v - 1, 0, true), (RequirementOp.Greater, v, 0, false),
        (RequirementOp.Less, v + 1, 0, true), (RequirementOp.Less, v, 0, false),
        // Both ends of a range count; a range typed high-to-low is the same range.
        (RequirementOp.Between, v, v + 2, true), (RequirementOp.Between, v - 2, v, true), (RequirementOp.Between, v + 2, v - 1, true),
        (RequirementOp.Between, v + 1, v + 3, false), (RequirementOp.Between, v - 3, v - 1, false),
        (RequirementOp.Outside, v + 1, v + 3, true), (RequirementOp.Outside, v - 3, v - 1, true),
        (RequirementOp.Outside, v, v + 2, false), (RequirementOp.Outside, v - 2, v, false),
    ];
}

public class RequirementMatrixNumberTests
{
    // Property, the board, the value its card has.
    public static readonly (string Property, string Setup, int Value)[] Numbers =
    [
        ("atk", "stat", 2000), ("def", "stat", 1200), ("base_atk", "stat", 2000), ("base_def", "stat", 1200),
        ("level", "stat", 4), ("orig_level", "stat", 4), ("card_level", "stat", 4), ("rank", "xyz", 4), ("link", "link", 2), ("scale", "pendulum", 3),
        ("materials", "xyz_summoned", 2), ("equips", "equipped2", 2), ("linked", "pointed_at", 1), ("attacks", "direct_attack", 1),
        // 3 counters of one kind; the requirement without a kind counts every counter.
        ("counter_count", "counters", 3),
    ];

    public static IEnumerable<object[]> Matrix() =>
        from n in Numbers from c in RequirementMatrix.Around(n.Value) select new object[] { n.Property, n.Setup, c.Op, c.A, c.B, c.Match };

    [EngineTheory]
    [MemberData(nameof(Matrix))]
    public void EveryComparison(string property, string setup, RequirementOp op, int a, int b, bool match)
    {
        var req = RequirementMatrix.R(property, op, a, b);
        // A monster has declared no attacks before the Battle Phase: the count is looked at only once it attacked.
        if (property == "attacks") RequirementMatrix.Run(setup, match, req, RequirementMatrix.Is("attacked_turn"));
        else RequirementMatrix.Run(setup, match, req);
    }

    public static IEnumerable<object[]> Guards() =>
    [
        // A Spell or Trap has 0 ATK, DEF, Level... to the engine; a requirement on them must not pick it.
        .. new[] { "atk", "def", "base_atk", "base_def", "level", "orig_level", "rank", "link", "scale", "materials", "equips", "linked", "attacks" }
            .SelectMany(p => new object[][] { [p, "spell_any", RequirementOp.LessOrEqual, 0, false], [p, "trap_any", RequirementOp.Equal, 0, false] }),
        // ... but a monster with 0 does count.
        ["atk", "zero_any", RequirementOp.Equal, 0, true], ["def", "zero_any", RequirementOp.LessOrEqual, 0, true],
        ["base_atk", "zero_any", RequirementOp.Equal, 0, true], ["base_def", "zero_any", RequirementOp.Equal, 0, true],
        ["equips", "zero_any", RequirementOp.Equal, 0, true], ["attacks", "zero_any", RequirementOp.Equal, 0, true],
        // Xyz Monsters have no Level, Link Monsters no Level and no DEF.
        ["level", "xyz", RequirementOp.LessOrEqual, 4, false], ["orig_level", "xyz", RequirementOp.LessOrEqual, 4, false],
        ["level", "link", RequirementOp.LessOrEqual, 4, false], ["orig_level", "link", RequirementOp.LessOrEqual, 4, false],
        ["def", "link", RequirementOp.LessOrEqual, 0, false], ["base_def", "link", RequirementOp.Equal, 0, false],
        ["def", "xyz", RequirementOp.Equal, 1800, true],
        // Only Xyz Monsters have a Rank or materials, only Link Monsters a Link Rating or monsters they point to,
        // only Pendulum Monsters a Scale.
        ["rank", "stat", RequirementOp.LessOrEqual, 4, false], ["rank", "link", RequirementOp.LessOrEqual, 4, false],
        ["link", "stat", RequirementOp.LessOrEqual, 4, false], ["link", "xyz", RequirementOp.LessOrEqual, 4, false],
        ["scale", "stat", RequirementOp.LessOrEqual, 4, false], ["materials", "stat", RequirementOp.Equal, 0, false],
        ["materials", "xyz", RequirementOp.Equal, 0, true], ["linked", "stat", RequirementOp.Equal, 0, false], ["linked", "link", RequirementOp.Equal, 0, true],
        // A Pendulum Monster in the Pendulum Zone is a Spell with a Scale, not a monster with ATK.
        ["scale", "pzone_any", RequirementOp.Equal, 3, true], ["atk", "pzone_any", RequirementOp.GreaterOrEqual, 0, false],
        // Current values against original ones (+500 ATK/DEF, +1 Level).
        ["atk", "boosted", RequirementOp.Equal, 2500, true], ["atk", "boosted", RequirementOp.Equal, 2000, false],
        ["base_atk", "boosted", RequirementOp.Equal, 2000, true], ["base_atk", "boosted", RequirementOp.Equal, 2500, false],
        ["def", "boosted", RequirementOp.Equal, 1700, true], ["base_def", "boosted", RequirementOp.Equal, 1200, true], ["base_def", "boosted", RequirementOp.Equal, 1700, false],
        ["level", "boosted", RequirementOp.Equal, 5, true], ["orig_level", "boosted", RequirementOp.Equal, 4, true], ["orig_level", "boosted", RequirementOp.Equal, 5, false],
        // A Monster Card's Level is its current one in a Monster Zone; Spells and Traps have none.
        ["card_level", "boosted", RequirementOp.Equal, 5, true], ["card_level", "spell_any", RequirementOp.LessOrEqual, 0, false],
    ];

    [EngineTheory]
    [MemberData(nameof(Guards))]
    public void OnlyCardsThatHaveTheProperty(string property, string setup, RequirementOp op, int value, bool match) =>
        RequirementMatrix.Run(setup, match, RequirementMatrix.R(property, op, value, value));
}

public class RequirementMatrixChoiceTests
{
    private const long Dark = (long)CardAttribute.Dark, Light = (long)CardAttribute.Light, Wind = (long)CardAttribute.Wind;
    private const long Fiend = (long)CardRace.Fiend, WarriorRace = (long)CardRace.Warrior, Aqua = (long)CardRace.Aqua;

    private static object[] Row(string setup, bool match, params CardRequirement[] reqs) => [setup, match, reqs];

    private static CardRequirement Is(string p, params long[] v) => RequirementMatrix.Is(p, v);
    private static CardRequirement IsNot(string p, params long[] v) => RequirementMatrix.IsNot(p, v);

    public static IEnumerable<object[]> Flags() =>
    [
        // Attribute: one of the picked ones, or none of them.
        Row("stat", true, Is("attribute", Dark)), Row("stat", false, Is("attribute", Light, Wind)), Row("stat", true, Is("attribute", Light, Dark)),
        Row("stat", false, IsNot("attribute", Dark)), Row("stat", false, IsNot("attribute", Light, Dark)), Row("stat", true, IsNot("attribute", Light, Wind)),
        Row("boosted", true, Is("attribute", Light)), Row("boosted", false, Is("attribute", Dark)),
        Row("stat", true, Is("orig_attribute", Dark)), Row("stat", false, Is("orig_attribute", Light, Wind)), Row("stat", true, Is("orig_attribute", Wind, Dark)),
        Row("stat", false, IsNot("orig_attribute", Dark)), Row("stat", false, IsNot("orig_attribute", Light, Dark)), Row("stat", true, IsNot("orig_attribute", Light, Wind)),
        Row("boosted", true, Is("orig_attribute", Dark)), Row("boosted", false, Is("orig_attribute", Light)), Row("boosted", true, IsNot("orig_attribute", Light)),
        // Type.
        Row("stat", true, Is("race", Fiend)), Row("stat", false, Is("race", WarriorRace, Aqua)), Row("stat", true, Is("race", Aqua, Fiend)),
        Row("stat", false, IsNot("race", Fiend)), Row("stat", false, IsNot("race", WarriorRace, Fiend)), Row("stat", true, IsNot("race", WarriorRace, Aqua)),
        Row("boosted", true, Is("race", WarriorRace)), Row("boosted", false, Is("race", Fiend)),
        Row("stat", true, Is("orig_race", Fiend)), Row("stat", false, Is("orig_race", WarriorRace, Aqua)), Row("stat", true, Is("orig_race", Aqua, Fiend)),
        Row("stat", false, IsNot("orig_race", Fiend)), Row("stat", false, IsNot("orig_race", Aqua, Fiend)), Row("stat", true, IsNot("orig_race", WarriorRace, Aqua)),
        Row("boosted", true, Is("orig_race", Fiend)), Row("boosted", false, Is("orig_race", WarriorRace)),
        // Monster card types.
        Row("xyz", true, Is("monster_type", 0x800000)), Row("xyz", false, Is("monster_type", 0x40, 0x4000000)), Row("xyz", true, Is("monster_type", 0x40, 0x800000)),
        Row("xyz", false, IsNot("monster_type", 0x800000)), Row("xyz", false, IsNot("monster_type", 0x40, 0x800000)), Row("xyz", true, IsNot("monster_type", 0x40, 0x4000000)),
        Row("stat", true, Is("monster_type", 0x10)), Row("stat", false, Is("monster_type", 0x20)), Row("effect", true, Is("monster_type", 0x20)),
        Row("effect", false, IsNot("monster_type", 0x20)), Row("tuner", true, Is("monster_type", 0x1000)), Row("stat", false, Is("monster_type", 0x1000)),
        Row("link", true, Is("monster_type", 0x4000000)), Row("pendulum", true, Is("monster_type", 0x1000000)), Row("stat", true, IsNot("monster_type", 0x1000000, 0x4000)),
        // Monster, Spell or Trap.
        Row("spell_any", true, Is("card_type", 0x2)), Row("spell_any", false, Is("card_type", 0x1, 0x4)), Row("spell_any", true, Is("card_type", 0x2, 0x4)),
        Row("spell_any", false, IsNot("card_type", 0x2)), Row("spell_any", false, IsNot("card_type", 0x1, 0x2)), Row("spell_any", true, IsNot("card_type", 0x1, 0x4)),
        // What kind of Spell or Trap: the filler Spell and Trap are Normal ones.
        Row("spell_any", true, Is("st_kind", 0)), Row("spell_any", false, Is("st_kind", 3, 4)), Row("spell_any", false, IsNot("st_kind", 0)),
        Row("spell_any", true, IsNot("st_kind", 3)), Row("trap_any", true, Is("st_kind", 6)), Row("trap_any", false, Is("st_kind", 0, 7)),
        Row("trap_any", true, Is("card_type", 0x4)), Row("stat_any", true, Is("card_type", 0x1)), Row("stat_any", false, Is("card_type", 0x2, 0x4)),
        // Position (cards on the field start face-up in Attack Position; Spells and Traps face-down).
        Row("stat", true, Is("position", 0x3)), Row("stat", false, Is("position", 0xc)), Row("stat", false, Is("position", 0xc, 0xa)), Row("stat", true, Is("position", 0xc, 0x5)),
        Row("stat", false, IsNot("position", 0x3)), Row("stat", false, IsNot("position", 0xc, 0x5)), Row("stat", true, IsNot("position", 0xc, 0xa)),
        Row("defense", true, Is("position", 0xc)), Row("defense", false, Is("position", 0x3)), Row("defense", true, IsNot("position", 0x3)),
        Row("spell_any", true, Is("position", 0xa)), Row("spell_any", false, Is("position", 0x5)), Row("spell_any", false, IsNot("position", 0xa)),
        // Where it was Summoned from.
        Row("normal_summoned", true, Is("summoned_from", 0x2)), Row("normal_summoned", false, Is("summoned_from", 0x1, 0x10)), Row("normal_summoned", true, Is("summoned_from", 0x2, 0x10)),
        Row("normal_summoned", false, IsNot("summoned_from", 0x2)), Row("normal_summoned", false, IsNot("summoned_from", 0x2, 0x10)), Row("normal_summoned", true, IsNot("summoned_from", 0x1, 0x10)),
        Row("sp_from_deck", true, Is("summoned_from", 0x1)), Row("sp_from_deck", false, Is("summoned_from", 0x2, 0x10)),
        Row("sp_from_grave", true, Is("summoned_from", 0x10)), Row("sp_from_grave", false, IsNot("summoned_from", 0x10)),
        Row("sp_from_banished", true, Is("summoned_from", 0x20)), Row("sp_from_banished", false, Is("summoned_from", 0x10)),
        Row("xyz_summoned", true, Is("summoned_from", 0x40)), Row("xyz_summoned", false, IsNot("summoned_from", 0x40)), Row("xyz_summoned", true, IsNot("summoned_from", 0x2, 0x1)),
        // Never Summoned (put on the field as the duel starts): not Summoned from anywhere.
        Row("stat", false, Is("summoned_from", 0x2, 0x1, 0x10, 0x20, 0x40)), Row("stat", true, IsNot("summoned_from", 0x2)),
        // What the card is printed as: a Pendulum Monster in the Pendulum Zone is a Spell now, but originally a monster.
        Row("stat", true, Is("orig_card_type", 0x1)), Row("stat", false, Is("orig_card_type", 0x2, 0x4)), Row("stat", true, Is("orig_card_type", 0x4, 0x1)),
        Row("stat", false, IsNot("orig_card_type", 0x1)), Row("stat", false, IsNot("orig_card_type", 0x2, 0x1)), Row("stat", true, IsNot("orig_card_type", 0x2, 0x4)),
        Row("spell_any", true, Is("orig_card_type", 0x2)), Row("spell_any", false, Is("orig_card_type", 0x1)), Row("trap_any", true, Is("orig_card_type", 0x4)),
        Row("xyz", true, Is("orig_card_type", 0x800000)), Row("stat", false, Is("orig_card_type", 0x800000)),
        Row("pzone_any", true, Is("orig_card_type", 0x1)), Row("pzone_any", true, Is("orig_card_type", 0x1000000)), Row("pzone_any", false, Is("card_type", 0x1)),
        // Where it is.
        Row("stat", true, Is("location", 0x4)), Row("stat", false, Is("location", 0x10, 0x2)), Row("stat", true, Is("location", 0x10, 0x4)),
        Row("stat", false, IsNot("location", 0x4)), Row("stat", false, IsNot("location", 0x8, 0x4)), Row("stat", true, IsNot("location", 0x8, 0x10)),
        Row("spell_any", true, Is("location", 0x8)), Row("spell_any", false, Is("location", 0x4)),
        Row("in_grave", true, Is("location", 0x10)), Row("in_grave", false, Is("location", 0x4, 0x2)), Row("in_grave", false, IsNot("location", 0x10)),
        Row("in_grave", true, IsNot("location", 0x20)),
    ];

    public static IEnumerable<object[]> Options() =>
    [
        // How it was Summoned. A Tribute Summon is a Normal Summon; every Extra Deck Summon is a Special Summon.
        Row("normal_summoned", true, Is("summon", 0)), Row("normal_summoned", false, Is("summon", 1, 3)), Row("normal_summoned", true, Is("summon", 3, 0)),
        Row("normal_summoned", false, IsNot("summon", 0)), Row("normal_summoned", false, IsNot("summon", 3, 0)), Row("normal_summoned", true, IsNot("summon", 1, 3)),
        Row("tribute_summoned", true, Is("summon", 1)), Row("tribute_summoned", true, Is("summon", 0)), Row("tribute_summoned", false, Is("summon", 3)),
        Row("flip_summoned", true, Is("summon", 2)), Row("normal_summoned", false, Is("summon", 2)),
        Row("sp_from_deck", true, Is("summon", 3)), Row("sp_from_deck", false, Is("summon", 0)), Row("sp_from_deck", false, IsNot("summon", 3)),
        Row("fusion_summoned", true, Is("summon", 4)), Row("fusion_summoned", false, Is("summon", 5)), Row("fusion_summoned", false, IsNot("summon", 4)),
        Row("synchro_summoned", true, Is("summon", 5)), Row("synchro_summoned", false, Is("summon", 6, 7)), Row("synchro_summoned", true, IsNot("summon", 4, 6)),
        Row("xyz_summoned", true, Is("summon", 6)), Row("xyz_summoned", true, Is("summon", 3)), Row("xyz_summoned", false, Is("summon", 4, 5)), Row("xyz_summoned", true, Is("summon", 4, 6)),
        Row("xyz_summoned", false, IsNot("summon", 6)), Row("xyz_summoned", false, IsNot("summon", 4, 6)), Row("xyz_summoned", true, IsNot("summon", 4, 5)),
        Row("link_summoned", true, Is("summon", 7)), Row("link_summoned", false, Is("summon", 6)),
        Row("ritual_summoned", true, Is("summon", 8)), Row("ritual_summoned", false, Is("summon", 4)),
        Row("pendulum_summoned", true, Is("summon", 9)), Row("pendulum_summoned", false, Is("summon", 8)),
        // Never Summoned: none of them.
        Row("stat", false, Is("summon", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9)), Row("stat", true, IsNot("summon", 0, 3)),
        // Who owns it.
        Row("stat", true, Is("owner", 0)), Row("stat", false, Is("owner", 1)), Row("stat", false, IsNot("owner", 0)), Row("stat", true, IsNot("owner", 1)),
        Row("control", true, Is("owner", 1)), Row("control", false, Is("owner", 0)), Row("control", true, IsNot("owner", 0)), Row("control", false, IsNot("owner", 1)),
        Row("control", true, Is("owner", 0, 1)), Row("control", false, IsNot("owner", 0, 1)),
        // Archetype and name.
        Row("stat", true, Is("archetype", RequirementMatrix.Arch)), Row("stat", false, Is("archetype", RequirementMatrix.OtherArch)),
        Row("stat", true, Is("archetype", RequirementMatrix.OtherArch, RequirementMatrix.Arch)), Row("stat", false, IsNot("archetype", RequirementMatrix.Arch)),
        Row("stat", true, IsNot("archetype", RequirementMatrix.OtherArch)), Row("effect", false, Is("archetype", RequirementMatrix.Arch)),
        Row("stat", true, Is("name", RequirementMatrix.S)), Row("stat", false, Is("name", RequirementMatrix.Other)), Row("stat", true, Is("name", RequirementMatrix.Other, RequirementMatrix.S)),
        Row("stat", false, IsNot("name", RequirementMatrix.S)), Row("stat", false, IsNot("name", RequirementMatrix.Other, RequirementMatrix.S)), Row("stat", true, IsNot("name", RequirementMatrix.Other)),
        // The original name: the subject's printed one.
        Row("stat", true, Is("orig_name", RequirementMatrix.S)), Row("stat", false, Is("orig_name", RequirementMatrix.Other)),
        Row("stat", false, IsNot("orig_name", RequirementMatrix.S)), Row("stat", true, IsNot("orig_name", RequirementMatrix.Other)),
        // The archetypes a Fusion Monster's materials name.
        Row("fusion_mentions", true, Is("material_archetype", RequirementMatrix.Arch)), Row("fusion_mentions", false, Is("material_archetype", RequirementMatrix.OtherArch)),
        Row("fusion_mentions", false, IsNot("material_archetype", RequirementMatrix.Arch)), Row("fusion_mentions", true, IsNot("material_archetype", RequirementMatrix.OtherArch)),
        Row("fusion_plain", false, Is("material_archetype", RequirementMatrix.Arch)), Row("fusion_plain", true, IsNot("material_archetype", RequirementMatrix.Arch)),
        // Whether a counter can be placed on it: the subject allows no Spell Counters; A-Counters go on any card.
        Row("stat", false, Is("can_have_counter", 0x1)), Row("stat", true, IsNot("can_have_counter", 0x1)),
        Row("stat", true, Is("can_have_counter", 0x100e)), Row("stat", false, IsNot("can_have_counter", 0x100e)),
    ];

    public static IEnumerable<object[]> States() =>
    [
        Row("negated_by_spell", true, Is("negated")), Row("opp_effect", false, Is("negated")), Row("opp_effect", true, IsNot("negated")),
        Row("negated_rig", true, Is("negated")), Row("negated_rig", false, IsNot("negated")),
        Row("normal_summoned", true, Is("summoned_turn")), Row("normal_summoned", false, IsNot("summoned_turn")), Row("sp_from_grave", true, Is("summoned_turn")),
        Row("xyz_summoned", true, Is("summoned_turn")), Row("stat", false, Is("summoned_turn")), Row("stat", true, IsNot("summoned_turn")),
        // Special Summoned this turn: not a Normal Summon.
        Row("sp_from_grave", true, Is("spsummoned_turn")), Row("sp_from_grave", false, IsNot("spsummoned_turn")), Row("xyz_summoned", true, Is("spsummoned_turn")),
        Row("normal_summoned", false, Is("spsummoned_turn")), Row("normal_summoned", true, IsNot("spsummoned_turn")), Row("stat", false, Is("spsummoned_turn")),
        // Normal or Special Summoned this turn (Mimighoul Dungeon): either kind, but not a card that was never Summoned.
        Row("normal_summoned", true, Is("normal_special_turn")), Row("sp_from_grave", true, Is("normal_special_turn")), Row("xyz_summoned", true, Is("normal_special_turn")),
        Row("normal_summoned", false, IsNot("normal_special_turn")), Row("stat", false, Is("normal_special_turn")), Row("stat", true, IsNot("normal_special_turn")),
        // Declared an attack / battled. To the engine (and the rulings: Gladiator Beasts tag out after attacking directly)
        // a direct attack is a battle too; the attacked monster battled without attacking.
        Row("direct_attack", true, Is("attacked_turn")), Row("no_attack", false, Is("attacked_turn")), Row("battle", true, Is("attacked_turn")),
        Row("battle_attacked", false, Is("attacked_turn")), Row("battle_attacked", true, IsNot("attacked_turn"), Is("battled_turn")),
        Row("battle", false, IsNot("attacked_turn"), Is("battled_turn")), Row("no_attack", true, IsNot("attacked_turn")),
        Row("battle", true, Is("battled_turn")), Row("battle_attacked", true, Is("battled_turn")), Row("direct_attack", true, Is("battled_turn")),
        Row("no_attack", false, Is("battled_turn")), Row("no_attack", true, IsNot("battled_turn")),
        Row("direct_attack", false, Is("attacked_turn"), IsNot("battled_turn")), Row("battle", false, Is("attacked_turn"), IsNot("battled_turn")),
        Row("boosted", true, Is("changed_atk")), Row("boosted", false, IsNot("changed_atk")), Row("stat", false, Is("changed_atk")), Row("stat", true, IsNot("changed_atk")),
        Row("spell_any", false, IsNot("changed_atk")), Row("spell_any", false, Is("changed_atk")),
        Row("pointed_at_normal", true, Is("is_linked")), Row("pointed_at_normal", false, IsNot("is_linked")),
        Row("pointed_away_normal", false, Is("is_linked")), Row("pointed_away_normal", true, IsNot("is_linked")),
        Row("equipped1", true, Is("equipped")), Row("equipped1", false, IsNot("equipped")), Row("stat", false, Is("equipped")), Row("stat", true, IsNot("equipped")),
        Row("xyz_summoned", true, Is("has_materials")), Row("xyz_summoned", false, IsNot("has_materials")), Row("xyz", false, Is("has_materials")), Row("xyz", true, IsNot("has_materials")),
        Row("link_summoned", true, Is("extra_zone")), Row("link_summoned", false, IsNot("extra_zone")), Row("pointed_at", true, Is("extra_zone")),
        Row("link", false, Is("extra_zone")), Row("link", true, IsNot("extra_zone")),
        Row("link_summoned", false, Is("main_zone")), Row("link_summoned", true, IsNot("main_zone")), Row("link", true, Is("main_zone")), Row("link", false, IsNot("main_zone")),
        Row("stat", true, Is("targetable")), Row("stat", false, IsNot("targetable")), Row("untargetable", false, Is("targetable")), Row("untargetable", true, IsNot("targetable")),
        Row("counters", true, Is("counters")), Row("counters", false, IsNot("counters")), Row("stat", false, Is("counters")), Row("stat", true, IsNot("counters")),
        // Heretic Eradicator: an Xyz Monster cannot be Normal Summoned/Set; a Main Deck monster can.
        Row("stat", true, Is("summonable_card")), Row("stat", false, IsNot("summonable_card")), Row("xyz", false, Is("summonable_card")), Row("xyz", true, IsNot("summonable_card")),
        // Gizmek Taniguku: ATK equal to its own DEF (0 and 0 too); a Link Monster has no DEF to equal.
        Row("zero_any", true, Is("atk_eq_def")), Row("zero_any", false, IsNot("atk_eq_def")), Row("stat", false, Is("atk_eq_def")), Row("stat", true, IsNot("atk_eq_def")),
        Row("link", false, Is("atk_eq_def")),
        // Heretic Eradicator: an Xyz Monster cannot be Normal Summoned/Set; a Normal Monster can.
        Row("xyz", true, Is("not_summonable")), Row("xyz", false, IsNot("not_summonable")), Row("stat", false, Is("not_summonable")), Row("stat", true, IsNot("not_summonable")),
    ];

    [EngineTheory]
    [MemberData(nameof(Flags))]
    public void FlagChoices(string setup, bool match, CardRequirement[] reqs) => RequirementMatrix.Run(setup, match, reqs);

    [EngineTheory]
    [MemberData(nameof(Options))]
    public void OptionsArchetypesAndNames(string setup, bool match, CardRequirement[] reqs) => RequirementMatrix.Run(setup, match, reqs);

    [EngineTheory]
    [MemberData(nameof(States))]
    public void YesOrNoStates(string setup, bool match, CardRequirement[] reqs) => RequirementMatrix.Run(setup, match, reqs);

    [Fact]
    public void EveryPropertyAndComparison_IsTestedInTheEngine()
    {
        var tested = RequirementMatrixNumberTests.Numbers.Select(n => n.Property)
            .Concat(new[] { Flags(), Options(), States() }.SelectMany(rows => rows).SelectMany(r => ((CardRequirement[])r[2]).Select(q => q.Property)))
            .ToHashSet();
        // What an effect declared, and coin tossing and die rolling effects: ChanceAndDeclarationTests plays them; remembered cards, MemoryTests;
        // comparisons with "that card" (the target, the cost's card), CardRefTests.
        // Columns and names compared with other cards in the Spell & Trap Zone or GY, AngelechyTests.
        foreach (var p in CardProperties.All.Where(p => !CardProperties.IsDeclared(p.Key) && !CardRefs.IsRequirement(p.Key)
                     && p.Key is not ("coin_effect" or "dice_effect" or "remembered" or CardProperties.ColumnsAway or CardProperties.SameColumn or "unlike_names")))
        {
            Assert.Contains(p.Key, tested);
            if (p.Kind == PropertyKind.Number) continue;
            var ops = new[] { Flags(), Options(), States() }.SelectMany(rows => rows).SelectMany(r => (CardRequirement[])r[2]).Where(q => q.Property == p.Key).Select(q => q.Op).ToHashSet();
            Assert.True(p.Ops.All(ops.Contains), $"{p.Key}: {string.Join(", ", p.Ops.Where(o => !ops.Contains(o)))} untested");
        }
    }
}

/// <summary>What every property says on the card, and that every property with every comparison compiles cleanly.</summary>
public class RequirementMatrixTextTests
{
    private static CardRequirement R(string p, RequirementOp op, int v = 0, int v2 = 0) => RequirementMatrix.R(p, op, v, v2);
    private static CardRequirement Is(string p, params long[] v) => RequirementMatrix.Is(p, v);
    private static CardRequirement IsNot(string p, params long[] v) => RequirementMatrix.IsNot(p, v);

    public static IEnumerable<object[]> Texts() =>
    [
        [R("atk", RequirementOp.GreaterOrEqual, 2000), "with 2000 or more ATK"],
        [R("atk", RequirementOp.Outside, 2000, 1000), "whose ATK is less than 1000 or more than 2000"],
        [R("def", RequirementOp.LessOrEqual, 1000), "with 1000 or less DEF"],
        [R("def", RequirementOp.Less, 1000), "with less than 1000 DEF"],
        [R("base_atk", RequirementOp.Equal, 0), "with 0 original ATK"],
        [R("base_def", RequirementOp.Greater, 1500), "with more than 1500 original DEF"],
        [R("level", RequirementOp.Greater, 4), "whose Level is higher than 4"],
        [R("level", RequirementOp.Between, 5, 3), "whose Level is between 3 and 5"],
        [R("level", RequirementOp.GreaterOrEqual, 7), "whose Level is 7 or higher"],
        [R("orig_level", RequirementOp.Less, 5), "whose original Level is lower than 5"],
        [R("card_level", RequirementOp.GreaterOrEqual, 5), "whose Level is 5 or higher"],
        [R("rank", RequirementOp.Outside, 3, 5), "whose Rank is lower than 3 or higher than 5"],
        [R("link", RequirementOp.Equal, 2), "whose Link Rating is 2"],
        [R("scale", RequirementOp.NotEqual, 4), "whose Pendulum Scale is other than 4"],
        [R("materials", RequirementOp.Equal, 0), "with no materials attached"],
        [R("materials", RequirementOp.Equal, 1), "with 1 material attached"],
        [R("materials", RequirementOp.NotEqual, 2), "that does not have exactly 2 materials attached"],
        [R("materials", RequirementOp.Between, 1, 3), "with 1 to 3 materials attached"],
        [R("equips", RequirementOp.GreaterOrEqual, 1), "equipped with 1 or more Equip Cards"],
        [R("equips", RequirementOp.Equal, 1), "equipped with 1 Equip Card"],
        [R("linked", RequirementOp.Greater, 1), "that points to more than 1 monster"],
        [R("linked", RequirementOp.Equal, 0), "that points to no monsters"],
        [R("attacks", RequirementOp.Equal, 2), "that declared 2 attacks this turn"],
        [R("attacks", RequirementOp.Outside, 1, 2), "that declared less than 1 or more than 2 attacks this turn"],
        [Is("attribute", (long)CardAttribute.Dark, (long)CardAttribute.Light, (long)CardAttribute.Earth), "that is EARTH, LIGHT, or DARK"],
        [IsNot("attribute", (long)CardAttribute.Dark, (long)CardAttribute.Earth), "that is neither EARTH nor DARK"],
        [IsNot("orig_attribute", (long)CardAttribute.Dark, (long)CardAttribute.Light), "whose original Attribute is neither LIGHT nor DARK"],
        [Is("race", (long)CardRace.Aqua), "that is an Aqua monster"],
        [IsNot("race", (long)CardRace.Warrior, (long)CardRace.Spellcaster), "that is not a Warrior or Spellcaster monster"],
        [Is("orig_race", (long)CardRace.Dragon), "whose original Type is Dragon"],
        [Is("monster_type", 0x800000), "that is an Xyz Monster"],
        [IsNot("monster_type", 0x20, 0x40), "that is not an Effect Monster or Fusion Monster"],
        [Is("monster_type", 0x400), "that is a Union monster"],
        [Is("monster_type", 0x40, 0x2000), "that is a Fusion Monster or Synchro Monster"],
        [Is("card_type", 0x2, 0x4), "that is a Spell or Trap"],
        [Is("st_kind", 3, 4), "that is an Equip Spell or Field Spell"],
        // "Is not" is said with the card's ", except Equip Spells" (FilterText.ExceptText), not with the other requirements.
        [IsNot("st_kind", 3), "that is not an Equip Spell"],
        [IsNot("card_type", 0x1), "that is not a monster"],
        [Is("position", 0x3), "that is in Attack Position"],
        [Is("position", 0x5), "that is face-up"],
        [IsNot("position", 0xc, 0xa), "that is neither in Defense Position nor face-down"],
        [Is("summoned_from", 0x2), "that was Summoned from the hand"],
        [IsNot("summoned_from", 0x2, 0x10), "that was not Summoned from the hand or the GY"],
        [Is("summon", 1), "that was Tribute Summoned"],
        [IsNot("summon", 4, 5), "that was neither Fusion nor Synchro Summoned"],
        [Is("owner", 1), "that your opponent owns"],
        [IsNot("owner", 0), "that you do not own"],
        [Is("archetype", 0x7a1), "that is an \"Ember\" card"],
        [IsNot("archetype", 0x7a2, 0x7a1), "that is not a \"Flame\" or \"Ember\" card"],
        [Is("material_archetype", 0x7a1), "that mentions an \"Ember\" monster as material"],
        [Is("name", RequirementMatrix.S), "that is \"Subject\""],
        [IsNot("name", RequirementMatrix.S, RequirementMatrix.Other), "that is neither \"Subject\" nor \"Other\""],
        [Is("orig_name", RequirementMatrix.S), "whose original name is \"Subject\""],
        [Is("negated"), "whose effects are negated"],
        [Is("can_have_counter", 0x6e), "that you can place a Season Counter on"],
        [IsNot("summoned_turn"), "that was not Summoned this turn"],
        [Is("spsummoned_turn"), "that was Special Summoned this turn"],
        [IsNot("summonable_card"), "that cannot be Normal Summoned/Set"],
        [IsNot("normal_special_turn"), "that was not Normal or Special Summoned this turn"],
        [Is("attacked_turn"), "that declared an attack this turn"],
        [IsNot("battled_turn"), "that has not battled this turn"],
        [IsNot("changed_atk"), "whose ATK is the same as its original ATK"],
        [Is("is_linked"), "that is linked"],
        [IsNot("equipped"), "that is not equipped with an Equip Card"],
        [Is("has_materials"), "that has material"],
        [IsNot("extra_zone"), "not in the Extra Monster Zone"],
        [Is("main_zone"), "not in the Extra Monster Zone or the Field Zone"],
        [IsNot("main_zone"), "in the Extra Monster Zone or the Field Zone"],
        [Is("targetable"), "that can be targeted"],
        [Is("declared_name"), "with the declared name"],
        [IsNot("declared_orig_name"), "without the declared original name"],
        [Is("declared_race"), "with the declared Type"],
        [Is("declared_attribute"), "with the declared Attribute"],
        [IsNot("declared_level"), "without the declared Level"],
        [Is("declared_type"), "of the declared card type"],
        [Is("coin_effect"), "that has a coin tossing effect"],
        [IsNot("dice_effect"), "that does not require a die roll"],
        [Is("counters"), "that has a counter on it"],
        [IsNot("counters"), "that has no counters on it"],
        [R("counter_count", RequirementOp.GreaterOrEqual, 2), "with 2 or more counters on it"],
        [RequirementMatrix.R("counter_count", RequirementOp.Equal, 1, 0, [0x1]), "with 1 Spell Counter on it"],
        [Is("orig_card_type", 0x1), "that is originally a monster"],
        [IsNot("orig_card_type", 0x1000000), "that is not originally a Pendulum Monster"],
        [Is("location", 0x10), "in the GY"],
        [IsNot("location", 0x2, 0x10), "not in the hand or the GY"],
        [Is("remembered", 0), "that was remembered"],
        [IsNot("remembered", 1), "that was not remembered"],
        [Is("ref_attribute", (long)CardRef.Target), "with the same Attribute as that monster"],
        [IsNot("ref_attribute", (long)CardRef.Target), "with a different Attribute"],
        [IsNot("ref_race", (long)CardRef.Cost), "with a different Type"],
        [Is("ref_level", (long)CardRef.Cost), "with the same Level as that monster"],
        [IsNot("ref_name", (long)CardRef.Target), "with a different name"],
        [IsNot("ref_orig_attribute", (long)CardRef.Cost), "with a different original Attribute from that monster's"],
        [Is("ref_orig_level", (long)CardRef.Cost), "with the same original Level as that monster"],
        [IsNot("ref_orig_name", (long)CardRef.Cost), "with a different original name"],
        [Is("ref_atk_le", (long)CardRef.Target), "with ATK less than or equal to that monster's ATK"],
        [Is("ref_mentioned", (long)CardRef.Cost), "whose name is mentioned on that monster"],
        [Is("atk_eq_def"), "whose ATK equals its own DEF"],
        [R(CardProperties.ColumnsAway, RequirementOp.Equal, 0), "in this card's column"],
        [R(CardProperties.ColumnsAway, RequirementOp.Equal, 1), "in this card's adjacent column"],
        [R(CardProperties.ColumnsAway, RequirementOp.Equal, 2), "that is 2 columns away from this card's"],
        [R(CardProperties.ColumnsAway, RequirementOp.NotEqual, 0), "in another column"],
        [R(CardProperties.ColumnsAway, RequirementOp.LessOrEqual, 1), "in this card's column or its adjacent columns"],
        [SameColumn(RequirementOp.Is), "in the same column as the \"Ember\" monster(s) you control"],
        [SameColumn(RequirementOp.IsNot), "not in the same column as the \"Ember\" monster(s) you control"],
        [Is("unlike_names", 0), "with a different name from the cards in your Spell & Trap Zone"],
        [Is("unlike_names", 1), "with a different name from the cards in your GY"],
        [IsNot("unlike_names", 2), "with the same name as one of the cards you control"],
        [Is("ref_adjacent", (long)CardRef.Affected), "in its adjacent Monster Zones and/or Spell & Trap Zones"],
        [Is("ref_column", (long)CardRef.Target), "in the same column as that card"],
        [IsNot("not_summonable"), "that can be Normal Summoned/Set"],
        [Is("not_summonable"), "that cannot be Normal Summoned/Set"],
    ];

    /// <summary>"In the same column as the "Ember" monster(s) you control".</summary>
    private static CardRequirement SameColumn(RequirementOp op) => new()
    {
        Property = CardProperties.SameColumn, Op = op,
        Cards = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = 0x7a1 },
    };

    private static CardPack Pack() => new()
    {
        Cards = [RequirementMatrix.Mon(RequirementMatrix.S, "Subject"), RequirementMatrix.Mon(RequirementMatrix.Other, "Other")],
        Archetypes = [new Archetype { Code = 0x7a1, Name = "Ember" }, new Archetype { Code = 0x7a2, Name = "Flame" }],
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void Reads(CardRequirement r, string phrase)
    {
        Assert.Equal(phrase, CardProperties.Text(r, Pack()));
        // Choices cards write before the noun ("EARTH or LIGHT monster", "Xyz Monster", "non-DARK monster"): see BeforeTheNoun.
        if (r.Property is "attribute" or "race" or "archetype" or "monster_type" && (r.Op == RequirementOp.Is || r.Values.Count == 1)) return;
        // So does a Monster Card's Level: see CardLevel_ReadsBeforeTheNoun.
        if (r.Property == "card_level") return;
        var filter = RequirementMatrix.Monsters();
        // A Spell/Trap kind the cards are not: ", except Equip Spells", at the end.
        if (r is { Property: CardProperties.SpellTrapKind, Op: RequirementOp.IsNot })
        {
            filter.Requirements = [r];
            Assert.Equal("monster, except Equip Spells", FilterText.Describe(filter, Pack(), withLocation: false));
            return;
        }
        filter.Requirements = [r];
        Assert.Equal("monster " + phrase, FilterText.Describe(filter, Pack(), withLocation: false));
    }

    /// <summary>A Monster Card's Level reads as a Level does, before the noun: "a Level 5 or higher Monster Card".</summary>
    [Fact]
    public void CardLevel_ReadsBeforeTheNoun()
    {
        var filter = RequirementMatrix.Monsters();
        filter.MonsterCards = true;
        filter.Requirements = [R("card_level", RequirementOp.GreaterOrEqual, 5)];
        Assert.Equal("Level 5 or higher Monster Card", FilterText.Describe(filter, Pack(), withLocation: false));
        filter.Requirements = [R("card_level", RequirementOp.Greater, 4)];
        Assert.Equal("Monster Card whose Level is higher than 4", FilterText.Describe(filter, Pack(), withLocation: false));
    }

    public static IEnumerable<object[]> BeforeTheNoun() =>
    [
        [Is("attribute", (long)CardAttribute.Earth, (long)CardAttribute.Light), "EARTH or LIGHT monster"],
        [Is("race", (long)CardRace.Aqua, (long)CardRace.Fish, (long)CardRace.SeaSerpent), "Aqua, Fish, or Sea Serpent monster"],
        [Is("archetype", 0x7a1, 0x7a2), "\"Ember\" or \"Flame\" monster"],
        [Is("monster_type", 0x800000), "Xyz Monster"],
        [Is("monster_type", 0x40, 0x2000, 0x800000), "Fusion, Synchro, or Xyz Monster"],
        [Is("monster_type", 0x400), "monster that is a Union monster"],
        [IsNot("attribute", (long)CardAttribute.Dark), "non-DARK monster"],
        [IsNot("race", (long)CardRace.Fairy), "non-Fairy monster"],
        [IsNot("monster_type", 0x4000000), "non-Link monster"],
        [IsNot("monster_type", 0x4000), "non-Token monster"],
        [IsNot("archetype", 0x7a2), "non-\"Flame\" monster"],
        // An Attribute other than EARTH (IsNonAttribute).
        [Is("attribute", 0x2, 0x4, 0x8, 0x10, 0x20, 0x40), "non-EARTH monster"],
    ];

    /// <summary>A requirement that leaves out one Type, Attribute, archetype or kind reads as cards write it: "non-Fairy monster".</summary>
    [Theory]
    [MemberData(nameof(BeforeTheNoun))]
    public void SaysNonBeforeTheNoun(CardRequirement r, string text)
    {
        var filter = RequirementMatrix.Monsters();
        filter.Requirements = [r];
        Assert.Equal(text, FilterText.Describe(filter, Pack(), withLocation: false));
    }

    [Fact]
    public void AnyOfTheRequirements_ReadsWithOr_AndAgreesWithSeveralCards()
    {
        var filter = RequirementMatrix.Monsters();
        filter.Requirements = [Is("position", 0xa), IsNot("archetype", 0x7a1)];
        filter.AnyRequirement = true;
        Assert.Equal("monster that is face-down or that is not an \"Ember\" card", FilterText.Describe(filter, Pack(), withLocation: false));
        Assert.Equal("monsters that are face-down or that are not an \"Ember\" card", FilterText.Describe(filter, Pack(), plural: true, withLocation: false));
        // Every Attribute but one, among other choices.
        filter.Requirements = [Is("position", 0xa), Is("attribute", 0x2, 0x4, 0x8, 0x10, 0x20, 0x40)];
        Assert.Equal("monster that is face-down or that is non-EARTH", FilterText.Describe(filter, Pack(), withLocation: false));
    }

    [Fact]
    public void ANumberOnlyMonstersHave_MakesTheCardAMonster()
    {
        var filter = RequirementMatrix.AnyCard();
        filter.Zones = Zone.Graveyard;
        filter.AtkMin = 1;
        Assert.Equal("monster with 1 or more ATK", FilterText.Describe(filter, Pack(), withLocation: false));
        filter.AtkMin = null;
        filter.LevelMin = 1;
        Assert.Equal("monster with a Level", FilterText.Describe(filter, Pack(), withLocation: false));
    }

    [Fact]
    public void EveryProperty_HasText()
    {
        var covered = Texts().Select(t => ((CardRequirement)t[0]).Property).ToHashSet();
        Assert.All(CardProperties.All, p => Assert.Contains(p.Key, covered));
    }

    [Fact]
    public void Sentence()
    {
        var filter = RequirementMatrix.Monsters();
        filter.FaceUpOnly = true;
        filter.Requirements = [Is("monster_type", 0x800000), R("materials", RequirementOp.Equal, 0)];
        Assert.Contains("you control a face-up Xyz Monster with no materials attached: ",
            EffectTextGenerator.Generate(RequirementMatrix.TesterCard(RequirementMatrix.Exists(filter))));
    }

    [Fact]
    public void Keys_AreUnique()
    {
        var dupes = CardProperties.All.GroupBy(p => p.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    /// <summary>Every property with every comparison: compiles, passes the checker, and needs the effect only when it says so.</summary>
    [Fact]
    public void EveryPropertyAndComparison_Compiles()
    {
        foreach (var p in CardProperties.All)
            foreach (var op in p.Ops)
            {
                var values = p.Kind switch
                {
                    // Comparisons with "that card": the monster this card battles needs no other block.
                    PropertyKind.Options when CardRefs.IsRequirement(p.Key) => [(long)CardRef.BattleTarget],
                    PropertyKind.Flags or PropertyKind.Options => p.Choices!.Take(2).Select(c => c.Value).ToArray(),
                    PropertyKind.Archetype => [0x7a1L, 0x7a2L],
                    PropertyKind.CardName => [RequirementMatrix.S, RequirementMatrix.Other],
                    _ => [],
                };
                var r = RequirementMatrix.R(p.Key, op, 2, 5, values);
                // "In the same column as" the cards of its own picker.
                if (p.Key == CardProperties.SameColumn) r.Cards = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
                Assert.NotNull(CardProperties.Lua(r));
                Assert.Equal(p.NeedsEffect, CardProperties.Lua(r, hasEffect: false) is null);
                Assert.NotEqual("", CardProperties.Text(r, null));
                var filter = RequirementMatrix.AnyCard();
                filter.Requirements = [r];
                var result = ScriptCompiler.Compile(RequirementMatrix.TesterCard(RequirementMatrix.Exists(filter)));
                Assert.False(result.HasErrors, $"{p.Key} {op}: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
                Assert.True(ScriptChecker.Check(result.Lua).Count == 0, $"{p.Key} {op}: {string.Join("; ", ScriptChecker.Check(result.Lua).Select(d => d.Message))}");
            }
    }
}
