using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Every live value (LP, counts, totals, the monster's own Level, materials, counters, Equip Cards, damage this turn,
/// "that amount"), every "how", every gain / lose / becomes of ATK, DEF and Level, and every target, played in the
/// real engine with the number checked exactly; plus the card text each one writes.
/// </summary>
public class ValueMatrixTests
{
    private const long Id = 100098001;
    /// <summary>A helper monster that changes the LP first (its effect activates before the value card's).</summary>
    private const long Pre = 100098002;
    private const long EquipId = 100098003;
    private const long Trigger = 100098004;
    private const long Reader = 100098005;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static EffectEvent Lasting(BlockInstance b) => new() { When = new BlockInstance("when.while_face_up"), Do = [b] };
    private static EffectEvent Ignition(BlockInstance b) => new() { When = new BlockInstance("when.ignition"), Do = [b] };

    private static CardProject Mon(long id, CardData? data, params EffectEvent[] effects)
    {
        var card = data ?? new CardData { Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1200 };
        card.Id = id;
        card.Name = $"Value Matrix {id}";
        var p = new CardProject { Card = card, Effects = [.. effects] };
        if (card.Frame == MonsterFrame.Xyz) p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 };
        return p;
    }

    private static string Atk(long code, int atk, int seq = -1) => seq < 0
        ? $"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{code}):GetAttack()=={atk}"
        : $"Duel.GetFieldCard(0,LOCATION_MZONE,{seq}):GetAttack()=={atk}";
    private static string Def(long code, int def) => $"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{code}):GetDefense()=={def}";
    private static string Level(long code, int lv) => $"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{code}):GetLevel()=={lv}";
    private static string OppAtk(long code, int atk) => $"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{code}):GetAttack()=={atk}";

    private static readonly CardFilter YourGy = new() { Owner = CardOwner.You, Zones = Zone.Graveyard };
    private static readonly CardFilter OppMonsters = new() { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };

    /// <summary>
    /// One value source's board: the value card's data, the extra cards, how the board is laid out and played, the
    /// "of" picker and the number it must come to. The value card starts with 1000 ATK (2000 for the Xyz).
    /// </summary>
    /// <param name="ThisTurn">Damage or LP gained "this turn": back to 0 next turn, so it is also read during the turn.</param>
    /// <param name="ActionWhen">The action's WHEN, when it isn't an Ignition Effect (it has to come after something else).</param>
    private sealed record Source(string Key, int Value, int BaseAtk, CardData? Data, CardProject[] Extra, CardFilter? Of, Action<EngineScenario> Board, Action<EngineScenario> Play,
        bool ThisTurn = false, string? ActionWhen = null);

    private static CardProject PreCard(params BlockInstance[] steps) => Mon(Pre, null, new EffectEvent { When = new BlockInstance("when.ignition"), Do = [.. steps] });
    private static CardProject PreCost(int pay) => Mon(Pre, null, new EffectEvent
    {
        When = new BlockInstance("when.ignition"), Cost = [B("cost.pay_lp", ("amount", pay.ToString()))], Do = [new BlockInstance("do.draw")],
    });

    private static void Decks(EngineScenario s) => s.Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4);
    private static Action<EngineScenario> PreFirst => s => s.Card(Pre, 0, "mzone").Card(Id, 0, "mzone");
    private static Action<EngineScenario> Alone => s => s.Card(Id, 0, "mzone");
    private static void PlayPre(EngineScenario s) => s.Activate(Pre);
    private static void Nothing(EngineScenario s) { }

    private static CardData XyzData => new() { Frame = MonsterFrame.Xyz, Level = 4, Atk = 2000, Def = 2000 };
    private static CardData LinkData(LinkMarker m) => new() { Frame = MonsterFrame.Link, Atk = 1000, LinkMarkers = m };

    private static readonly Dictionary<string, Source> Sources = new Source[]
    {
        // Pays 2000 LP: 6000 to 8000.
        new("lp_diff", 2000, 1000, null, [PreCost(2000)], null, PreFirst, PlayPre),
        new("lp_below", 2000, 1000, null, [PreCost(2000)], null, PreFirst, PlayPre),
        // Burns the opponent for 1500: 8000 to 6500.
        new("lp_above", 1500, 1000, null, [PreCard(B("do.damage", ("amount", "1500")))], null, PreFirst, PlayPre),
        new("your_lp", 6000, 1000, null, [PreCost(2000)], null, PreFirst, PlayPre),
        new("opp_lp", 6500, 1000, null, [PreCard(B("do.damage", ("amount", "1500")))], null, PreFirst, PlayPre),
        // 3 cards in the hand (the default picker).
        new("count", 3, 1000, null, [], null, s => s.Card(Id, 0, "mzone").Card(Fiend, 0, "hand", 3), Nothing),
        // GY: 2 Warriors (EARTH Warrior), a Dragon (LIGHT Dragon), a Fiend (DARK Fiend).
        new("different_attr", 3, 1000, null, [], YourGy, GyMix, Nothing),
        new("different_race", 3, 1000, null, [], YourGy, GyMix, Nothing),
        new("different_name", 3, 1000, null, [], YourGy, GyMix, Nothing),
        // The opponent's Warrior (1500) and Dragon (1600).
        new("sum_atk", 3100, 1000, null, [], OppMonsters, s => s.Card(Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Dragon, 1, "mzone"), Nothing),
        new("sum_def", 4000, 1000, null, [], YourGy, GyMix, Nothing),
        new("sum_level", 16, 1000, null, [], YourGy, GyMix, Nothing),
        new("its_level", 5, 1000, new CardData { Frame = MonsterFrame.Effect, Level = 5, Atk = 1000, Def = 1200 }, [], null, Alone, Nothing),
        new("its_rank", 4, 2000, XyzData, [], null, Alone, Nothing),
        new("its_link", 3, 1000, LinkData(LinkMarker.Left | LinkMarker.Right | LinkMarker.Top), [], null, Alone, Nothing),
        // Xyz Summoned with 2 Warriors.
        new("its_materials", 2, 2000, XyzData, [], null, s => s.Card(Warrior, 0, "mzone", 2).Card(Id, 0, "extra"), s => s.SpSummon(Id)),
        new("its_used", 2, 2000, XyzData, [], null, s => s.Card(Warrior, 0, "mzone", 2).Card(Id, 0, "extra"), s => s.SpSummon(Id)),
        // 3 counters of one kind and 2 of another: 5 counters.
        // Every counter on your field: 3 on this card, 2 on a Warrior; 4 on the opponent's don't count.
        new("sum_counters", 5, 1000, null, [], new CardFilter { Owner = CardOwner.You, Zones = Zone.Field }, s => s.Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone")
            .Setup($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):AddCounter(0x1001,3) Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior}):AddCounter(0x1002,2) Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{Fiend}):AddCounter(0x1002,4)"), Nothing),
        new("its_counters", 5, 1000, null, [], null, s => s.Card(Id, 0, "mzone")
            .Setup($"local c=Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}) c:AddCounter(0x1001,3) c:AddCounter(0x1002,2)"), Nothing),
        // An Equip Spell activated from the hand onto it; the action is "if this card is equipped with an Equip Card".
        new("its_equips", 1, 1000, null, [EquipSpell()], null, s => s.Card(Id, 0, "mzone").Card(EquipId, 0, "hand"), s => s.Activate(EquipId), ActionWhen: "equipped"),
        // Zones 0, 1, 2: Warrior, this Link monster (arrows left and right), Warrior.
        new("its_linked", 2, 1000, LinkData(LinkMarker.Left | LinkMarker.Right), [], null, s => s.Card(Warrior, 0, "mzone").Card(Id, 0, "mzone").Card(Warrior, 0, "mzone"), Nothing),
        new("its_orig_atk", 1000, 1000, null, [], null, Alone, Nothing),
        new("its_orig_def", 1200, 1000, null, [], null, Alone, Nothing),
        // Original ATK 1000, DEF 1200: the higher is the DEF.
        new("its_orig_higher", 1200, 1000, null, [], null, Alone, Nothing),
        new("damage_turn", 800, 1000, null, [PreCard(B("do.damage", ("who", "you"), ("amount", "800")))], null, PreFirst, PlayPre, ThisTurn: true),
        new("opp_damage_turn", 600, 1000, null, [PreCard(B("do.damage", ("amount", "600")))], null, PreFirst, PlayPre, ThisTurn: true),
        new("gained_turn", 900, 1000, null, [PreCard(B("do.gain_lp", ("amount", "900")))], null, PreFirst, PlayPre, ThisTurn: true),
    }.ToDictionary(s => s.Key);

    private static void GyMix(EngineScenario s) => s.Card(Id, 0, "mzone").Card(Warrior, 0, "grave", 2).Card(Dragon, 0, "grave").Card(Fiend, 0, "grave");

    private static CardProject EquipSpell() => new()
    {
        Card = new CardData { Id = EquipId, Name = "Value Matrix Equip", Kind = CardKind.Spell, SpellKind = SpellKind.Equip },
        Effects = [Lasting(B("passive.equipped", ("rule", "indestructible_battle")))],
    };

    // "That amount", a die roll's result and a toss's heads only exist while an effect resolves: their own tests play them
    // (this file for "that amount", ChanceAndDeclarationTests for the die and the coin).
    // What this card remembers needs a block that remembers it first: MemoryTests plays those.
    public static IEnumerable<object[]> SourceKeys() => ValueSources.Sources.Where(o => !ValueSources.AtResolution(o.Value) && !ValueSources.Remembered(o.Value)).Select(o => new object[] { o.Value });

    [Fact]
    public void EverySource_HasABoard() =>
        Assert.Equal(ValueSources.Sources.Select(o => o.Value).Where(v => !ValueSources.AtResolution(v) && !ValueSources.Remembered(v)).OrderBy(v => v), Sources.Keys.OrderBy(v => v));

    private static EngineScenario Scenario(Source src, BlockInstance block, bool action)
    {
        if (src.Of is not null) block.Filters[ValueSources.CardsKey] = src.Of.Clone();
        var effect = !action ? Lasting(block)
            : src.ActionWhen is { } when ? new EffectEvent { When = new BlockInstance(ThisCardEventTrigger.BlockId).With("event", when), Do = [block] }
            : Ignition(block);
        var card = Mon(Id, src.Data is null ? null : CloneData(src.Data), effect);
        // "This turn" values go back to 0 next turn, when the checks run: a reader burns the opponent for this card's ATK
        // while it is still this turn.
        var reader = B(LifeFromValueAction.BlockId, ("value", "sum_atk"));
        reader.Filters[ValueSources.CardsKey] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardCode = Id };
        CardProject[] extra = !action && src.ThisTurn ? [.. src.Extra, Mon(Reader, null, Ignition(reader))] : src.Extra;
        var s = new EngineScenario([card, .. extra]);
        src.Board(s);
        if (!action && src.ThisTurn) s.Card(Reader, 0, "mzone");
        Decks(s);
        src.Play(s);
        if (action) s.Activate(Id);
        if (!action && src.ThisTurn) s.Activate(Reader);
        return s;
    }

    private static CardData CloneData(CardData d) => new() { Frame = d.Frame, Level = d.Level, Atk = d.Atk, Def = d.Def, LinkMarkers = d.LinkMarkers };

    /// <summary>"This card gains ATK equal to ...": the lasting effect keeps the number up to date.</summary>
    [EngineTheory]
    [MemberData(nameof(SourceKeys))]
    public void Passive_EverySource(string key)
    {
        var src = Sources[key];
        var s = Scenario(src, B(StatsFromValuePassive.BlockId, ("value", key)), action: false);
        if (src.ThisTurn)
            // During the turn the opponent took this card's ATK as damage; next turn the ATK is back to normal.
            s.Check($"Duel.GetLP(1)=={8000 - (key == "opp_damage_turn" ? 600 : 0) - (src.BaseAtk + src.Value)}").Check(Atk(Id, src.BaseAtk));
        else
            s.Check(Atk(Id, src.BaseAtk + src.Value));
        s.AssertPasses();
    }

    /// <summary>"This card gains ATK equal to ..." once, worked out when the effect resolves.</summary>
    [EngineTheory]
    [MemberData(nameof(SourceKeys))]
    public void Action_EverySource(string key)
    {
        var src = Sources[key];
        var b = B(StatsFromValueAction.BlockId, ("value", key), ("duration", "always"));
        b.Filters["cards"] = new CardFilter { ThisCard = true };
        Scenario(src, b, action: true)
            .ExpectActivations(src.Extra.Length > 0 ? 2 : 1)
            .Check(Atk(Id, src.BaseAtk + src.Value)).AssertPasses();
    }

    /// <summary>"Inflict damage to your opponent equal to ...": the same values as damage.</summary>
    [EngineTheory]
    [MemberData(nameof(SourceKeys))]
    public void Burn_EverySource(string key)
    {
        var src = Sources[key];
        // The helper's own LP change to the opponent comes first.
        var before = key switch { "lp_above" or "opp_lp" => 6500, "opp_damage_turn" => 7400, _ => 8000 };
        // Half of the value, so "your opponent's LP" doesn't end the Duel.
        Scenario(src, B(LifeFromValueAction.BlockId, ("value", key), ("how", "half")), action: true)
            .Check($"Duel.GetLP(1)=={before - src.Value / 2}").AssertPasses();
    }

    // ---------------------------------------------------------------- how much: equal, half, double, for each

    [EngineTheory]
    [InlineData("equal", 1, 801)]
    [InlineData("half", 1, 400)]
    [InlineData("double", 1, 1602)]
    [InlineData("per", 3, 2403)]
    public void Hows_PassiveAndAction(string how, int amount, int gain)
    {
        var src = Sources["damage_turn"];
        var pre = PreCard(B("do.damage", ("who", "you"), ("amount", "801")));
        foreach (var action in new[] { false, true })
        {
            var b = action ? B(StatsFromValueAction.BlockId, ("duration", "always")) : B(StatsFromValuePassive.BlockId);
            if (action) b.Filters["cards"] = new CardFilter { ThisCard = true };
            b.Values["value"] = "damage_turn";
            b.Values["how"] = how;
            b.Values["amount"] = amount.ToString();
            // The lasting effect is read by the reader during the turn (see Passive_EverySource).
            Scenario(src with { Extra = [pre] }, b, action).Check(action ? Atk(Id, 1000 + gain) : $"Duel.GetLP(1)=={8000 - 1000 - gain}").AssertPasses();
        }
        var burn = B(LifeFromValueAction.BlockId, ("value", "damage_turn"), ("how", how), ("amount", amount.ToString()));
        Scenario(src with { Extra = [pre] }, burn, true).Check($"Duel.GetLP(1)=={8000 - gain}").AssertPasses();
    }

    // ---------------------------------------------------------------- gains / loses / becomes ATK, DEF, both, Level

    /// <summary>3 Warriors in the GY: 500 x 3 = 1500 ATK/DEF, or a Level of 3. The card has 1800 ATK, 1200 DEF, Level 4.</summary>
    [EngineTheory]
    [InlineData("gain", "atk", 3300, 1200, 4)]
    [InlineData("gain", "def", 1800, 2700, 4)]
    [InlineData("gain", "both", 3300, 2700, 4)]
    [InlineData("gain", "level", 1800, 1200, 7)]
    [InlineData("lose", "atk", 300, 1200, 4)]
    [InlineData("lose", "def", 1800, 0, 4)]
    [InlineData("lose", "both", 300, 0, 4)]
    [InlineData("lose", "level", 1800, 1200, 1)]
    [InlineData("becomes", "atk", 1500, 1200, 4)]
    [InlineData("becomes", "def", 1800, 1500, 4)]
    [InlineData("becomes", "both", 1500, 1500, 4)]
    [InlineData("becomes", "level", 1800, 1200, 3)]
    public void DirsAndStats_PassiveAndAction(string dir, string stat, int atk, int def, int level)
    {
        foreach (var action in new[] { false, true })
        {
            var b = action ? B(StatsFromValueAction.BlockId, ("duration", "always")) : B(StatsFromValuePassive.BlockId);
            if (action) b.Filters["cards"] = new CardFilter { ThisCard = true };
            b.Values["dir"] = dir;
            b.Values["stat"] = stat;
            b.Values["value"] = "count";
            if (stat != "level") { b.Values["how"] = "per"; b.Values["amount"] = "500"; }
            b.Filters[ValueSources.CardsKey] = YourGy.Clone();
            var card = Mon(Id, new CardData { Frame = MonsterFrame.Effect, Level = 4, Atk = 1800, Def = 1200 }, action ? Ignition(b) : Lasting(b));
            var s = new EngineScenario(card).Card(Id, 0, "mzone").Card(Warrior, 0, "grave", 3);
            Decks(s);
            if (action) s.Activate(Id).ExpectActivations(1);
            s.Check(Atk(Id, atk)).Check(Def(Id, def)).Check(Level(Id, level)).AssertPasses();
        }
    }

    // ---------------------------------------------------------------- who: picked monsters, the opponent's, targets, all

    [EngineFact]
    public void Passive_PickedMonsters_IncludesOnesThatArriveLater()
    {
        // Monsters you control gain 100 ATK for each card in your hand: 3 Fiends stay in the hand after the Warrior is Summoned.
        var b = B(StatsFromValuePassive.BlockId, ("how", "per"), ("amount", "100"), ("value", "count"));
        b.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        var s = new EngineScenario(Mon(Id, null, Lasting(b))).Card(Id, 0, "mzone").Card(Dragon, 0, "mzone").Card(Fiend, 0, "hand", 3).Card(Warrior, 0, "hand");
        Decks(s);
        s.Summon(Warrior)
            .Check(Atk(Id, 1300)).Check(Atk(Dragon, 1900)).Check(Atk(Warrior, 1800))
            // The opponent's monsters don't gain.
            .AssertPasses();
    }

    [EngineFact]
    public void Passive_OpponentsMonsters_Lose()
    {
        var b = B(StatsFromValuePassive.BlockId, ("dir", "lose"), ("how", "per"), ("amount", "100"), ("value", "count"));
        b.Filters["cards"] = OppMonsters.Clone();
        var s = new EngineScenario(Mon(Id, null, Lasting(b))).Card(Id, 0, "mzone").Card(Fiend, 0, "hand", 3).Card(Warrior, 1, "mzone").Card(Dragon, 1, "mzone");
        Decks(s);
        s.Check(Atk(Id, 1000)).Check(OppAtk(Warrior, 1200)).Check(OppAtk(Dragon, 1300)).AssertPasses();
    }

    [EngineFact]
    public void Passive_PickedMonsters_EachUsesItsOwnStats()
    {
        // Each monster you control gains ATK equal to half its own original ATK; each other one gains its own Level x 100.
        var half = B(StatsFromValuePassive.BlockId, ("how", "half"), ("value", "its_orig_atk"));
        half.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        var level = B(StatsFromValuePassive.BlockId, ("stat", "def"), ("how", "per"), ("amount", "100"), ("value", "its_level"));
        level.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true };
        var card = Mon(Id, null, Lasting(half), Lasting(level));
        var s = new EngineScenario(card).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Dragon, 0, "mzone");
        Decks(s);
        s.Check(Atk(Id, 1500)).Check(Atk(Warrior, 2250)).Check(Atk(Dragon, 2400))
            .Check(Def(Id, 1200)).Check(Def(Warrior, 1400)).AssertPasses();
    }

    [EngineFact]
    public void Action_TargetsAnOpponentsMonster_ForTheTurnOnly()
    {
        // Target 1 face-up monster your opponent controls; it loses ATK equal to its original ATK. One version lasts while
        // face-up, the other resets in the End Phase (the check comes at the start of the next turn).
        foreach (var (duration, expected) in new[] { ("always", 0), ("turn", 1500) })
        {
            var b = B(StatsFromValueAction.BlockId, ("dir", "lose"), ("value", "its_orig_atk"), ("target", "true"), ("duration", duration));
            b.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
            var s = new EngineScenario(Mon(Id, null, Ignition(b))).Card(Id, 0, "mzone").Card(Warrior, 1, "mzone");
            Decks(s);
            s.Activate(Id).ExpectActivations(1).Check(OppAtk(Warrior, expected)).AssertPasses();
        }
    }

    [EngineFact]
    public void Action_TargetsTwo_AndAll_AndTwoPicked()
    {
        // Target 2: they lose 500 ATK for each card in your hand (2 cards).
        var two = B(StatsFromValueAction.BlockId, ("count", "2"), ("dir", "lose"), ("how", "per"), ("amount", "500"), ("value", "count"), ("target", "true"), ("duration", "always"));
        two.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
        var s = new EngineScenario(Mon(Id, null, Ignition(two))).Card(Id, 0, "mzone").Card(Fiend, 0, "hand", 2).Card(Warrior, 1, "mzone").Card(Dragon, 1, "mzone");
        Decks(s);
        s.Activate(Id).ExpectActivations(1).Check(OppAtk(Warrior, 500)).Check(OppAtk(Dragon, 600)).AssertPasses();

        // All face-up monsters you control gain ATK equal to their own Level x 100... and DEF equal to the cards in the GY x 100.
        var all = B(StatsFromValueAction.BlockId, ("mode", "all"), ("how", "per"), ("amount", "100"), ("value", "its_level"), ("duration", "always"));
        var allDef = B(StatsFromValueAction.BlockId, ("mode", "all"), ("stat", "def"), ("how", "per"), ("amount", "100"), ("value", "count"), ("duration", "always"));
        allDef.Filters[ValueSources.CardsKey] = YourGy.Clone();
        var card = Mon(Id, new CardData { Frame = MonsterFrame.Effect, Level = 6, Atk = 1000, Def = 1200 }, new EffectEvent { When = new BlockInstance("when.ignition"), Do = [all, allDef] });
        s = new EngineScenario(card).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 0, "grave", 2).Card(Warrior, 1, "mzone");
        Decks(s);
        s.Activate(Id).ExpectActivations(1).Check(Atk(Id, 1600)).Check(Atk(Warrior, 1900)).Check(OppAtk(Warrior, 1500))
            .Check(Def(Id, 1400)).Check(Def(Warrior, 1200)).AssertPasses();

        // 2 face-up monsters you control (not targeted) each become ATK equal to double their own original DEF.
        var picked = B(StatsFromValueAction.BlockId, ("count", "2"), ("dir", "becomes"), ("how", "double"), ("value", "its_orig_def"), ("duration", "always"));
        s = new EngineScenario(Mon(Id, null, Ignition(picked))).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone");
        Decks(s);
        s.Activate(Id).ExpectActivations(1).Check(Atk(Id, 2400)).Check(Atk(Warrior, 2000)).AssertPasses();
    }

    // ---------------------------------------------------------------- damage and LP

    [EngineTheory]
    [InlineData("damage", "opponent", 8000, 7100)]
    [InlineData("damage", "you", 7100, 8000)]
    [InlineData("damage", "both", 7100, 7100)]
    [InlineData("gain", "you", 8900, 8000)]
    [InlineData("gain", "opponent", 8000, 8900)]
    [InlineData("gain", "both", 8900, 8900)]
    public void Life_WhoAndWhat(string what, string who, int yourLp, int oppLp)
    {
        // 300 for each card in your hand (3 cards).
        var b = B(LifeFromValueAction.BlockId, ("what", what), ("who", who), ("how", "per"), ("amount", "300"), ("value", "count"));
        var s = new EngineScenario(Mon(Id, null, Ignition(b))).Card(Id, 0, "mzone").Card(Fiend, 0, "hand", 3);
        Decks(s);
        s.Activate(Id).ExpectActivations(1).Check($"Duel.GetLP(0)=={yourLp}").Check($"Duel.GetLP(1)=={oppLp}").AssertPasses();
    }

    [EngineTheory]
    [InlineData("damage")]
    [InlineData("gain")]
    public void Life_NothingToCount_CantBeActivated(string what)
    {
        // No cards in the hand: 300 x 0 is nothing, so the effect can't be activated at all.
        var b = B(LifeFromValueAction.BlockId, ("what", what), ("how", "per"), ("amount", "300"), ("value", "count"));
        var s = new EngineScenario(Mon(Id, null, Ignition(b))).Card(Id, 0, "mzone");
        Decks(s);
        s.Activate(Id).ExpectActivations(0).Check("Duel.GetLP(0)==8000 and Duel.GetLP(1)==8000").AssertPasses();
    }

    [EngineFact]
    public void Life_ThisCardsOwnLevel()
    {
        // Both players gain LP equal to this card's Level x 250.
        var b = B(LifeFromValueAction.BlockId, ("what", "gain"), ("who", "both"), ("how", "per"), ("amount", "250"), ("value", "its_level"));
        var s = new EngineScenario(Mon(Id, null, Ignition(b))).Card(Id, 0, "mzone");
        Decks(s);
        s.Activate(Id).ExpectActivations(1).Check("Duel.GetLP(0)==9000 and Duel.GetLP(1)==9000").AssertPasses();
    }

    // ---------------------------------------------------------------- "that amount"

    [EngineFact]
    public void ThatAmount_AfterPayingLp()
    {
        // The helper pays 1000 LP; then "if you pay LP: inflict damage to your opponent equal to that amount" and
        // "... gain LP equal to double that amount".
        var burn = Mon(Trigger, null, new EffectEvent { When = new BlockInstance("when.event").With("event", "you_pay_lp"), Do = [B(LifeFromValueAction.BlockId, ("value", "event"))] });
        new EngineScenario(PreCost(1000), burn).Card(Pre, 0, "mzone").Card(Trigger, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Pre).Activate(Trigger).ExpectActivations(2)
            .Check("Duel.GetLP(0)==7000 and Duel.GetLP(1)==7000").AssertPasses();

        var heal = Mon(Trigger, null, new EffectEvent { When = new BlockInstance("when.event").With("event", "you_pay_lp"), Do = [B(LifeFromValueAction.BlockId, ("what", "gain"), ("who", "you"), ("how", "double"), ("value", "event"))] });
        new EngineScenario(PreCost(1000), heal).Card(Pre, 0, "mzone").Card(Trigger, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Pre).Activate(Trigger).ExpectActivations(2)
            .Check("Duel.GetLP(0)==9000 and Duel.GetLP(1)==8000").AssertPasses();
    }

    [EngineFact]
    public void ThatAmount_AfterTakingDamage_RaisesAtkAndSetsDef()
    {
        // The helper burns its controller for 800; "if you take damage: this card gains ATK equal to that amount, and its
        // DEF becomes equal to half that amount".
        var gain = B(StatsFromValueAction.BlockId, ("value", "event"), ("duration", "always"));
        gain.Filters["cards"] = new CardFilter { ThisCard = true };
        var def = B(StatsFromValueAction.BlockId, ("value", "event"), ("stat", "def"), ("dir", "becomes"), ("how", "half"), ("duration", "always"));
        def.Filters["cards"] = new CardFilter { ThisCard = true };
        var trig = Mon(Trigger, null, new EffectEvent { When = new BlockInstance("when.event").With("event", "you_damage"), Do = [gain, def] });
        new EngineScenario(PreCard(B("do.damage", ("who", "you"), ("amount", "800"))), trig).Card(Pre, 0, "mzone").Card(Trigger, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Pre).Activate(Trigger).ExpectActivations(2)
            .Check(Atk(Trigger, 1800)).Check(Def(Trigger, 400)).AssertPasses();
    }

    [Fact]
    public void ThatAmount_WithoutADamageOrLpWhen_Warns()
    {
        var r = ScriptCompiler.Compile(Mon(Id, null, Ignition(B(LifeFromValueAction.BlockId, ("value", "event")))));
        Assert.False(r.HasErrors);
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("That amount"));
        var ok = ScriptCompiler.Compile(Mon(Id, null, new EffectEvent { When = new BlockInstance("when.event").With("event", "opp_gain_lp"), Do = [B(LifeFromValueAction.BlockId, ("value", "event"))] }));
        Assert.DoesNotContain(ok.Diagnostics, d => d.Message.Contains("That amount"));
    }

    [EngineFact]
    public void Passive_FromAContinuousSpell()
    {
        // A Continuous Spell: "monsters you control gain 100 ATK for each card in your hand" (2 Fiends).
        var b = B(StatsFromValuePassive.BlockId, ("how", "per"), ("amount", "100"), ("value", "count"));
        b.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        var spell = new CardProject { Card = new CardData { Id = Id, Name = "Value Matrix Spell", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous }, Effects = [Lasting(b)] };
        var s = new EngineScenario(spell).Card(Id, 0, "szone").Card(Warrior, 0, "mzone").Card(Fiend, 0, "hand", 2).Card(Dragon, 1, "mzone");
        Decks(s);
        s.Activate(Id).ExpectActivations(1).Check(Atk(Warrior, 1700)).Check(OppAtk(Dragon, 1600)).AssertPasses();
    }

    [EngineFact]
    public void Counters_AreAllCounted_NotTheirKinds()
    {
        // 3 counters of one kind and 2 of another: Card.GetCounter(0) says 2 (the kinds); there are 5 counters.
        var per = B("passive.self_stats_per", ("per", "counters"), ("amount", "100"));
        var s = new EngineScenario(Mon(Id, null, Lasting(per))).Card(Id, 0, "mzone")
            .Setup($"local c=Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}) c:AddCounter(0x1001,3) c:AddCounter(0x1002,2)");
        Decks(s);
        s.Check(Atk(Id, 1500)).AssertPasses();
    }

    [Fact]
    public void Passive_OnThisCard_NeedsAMonster()
    {
        var spell = new CardProject { Card = new CardData { Id = Id, Name = "Value Matrix Spell", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous }, Effects = [Lasting(B(StatsFromValuePassive.BlockId))] };
        Assert.True(ScriptCompiler.Compile(spell).HasErrors);
    }

    // ---------------------------------------------------------------- card text

    private static BlockInstance F(BlockInstance b, string key, CardFilter f) { b.Filters[key] = f; return b; }
    private static CardFilter YourField => new() { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
    private static CardFilter OppFaceUp => new() { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    public static IEnumerable<object[]> Texts()
    {
        // Lasting effects.
        yield return [Lasting(B(StatsFromValuePassive.BlockId)), "This card gains ATK equal to the difference between both players' LP."];
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("how", "per"), ("value", "count"))), "This card gains 300 ATK for each card in your hand."];
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("how", "per"), ("amount", "200"), ("value", "its_level"))), "This card gains ATK equal to its Level x 200."];
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("how", "per"), ("amount", "100"), ("value", "lp_diff"))), "This card gains ATK equal to the difference between both players' LP x 100."];
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("how", "per"), ("value", "different_name"))), "This card gains 300 ATK for each different card name among the cards in your hand."];
        yield return [Lasting(F(B(StatsFromValuePassive.BlockId, ("how", "per"), ("amount", "100"), ("value", "count")), "cards", YourField)), "Monsters you control gain 100 ATK for each card in your hand."];
        yield return [Lasting(F(B(StatsFromValuePassive.BlockId, ("dir", "lose"), ("how", "per"), ("amount", "100"), ("value", "count")), "cards", OppMonsters.Clone())), "Monsters your opponent controls lose 100 ATK for each card in your hand."];
        yield return [Lasting(F(B(StatsFromValuePassive.BlockId, ("how", "half"), ("value", "its_orig_atk")), "cards", YourField)), "Each monster you control gains ATK equal to half its original ATK."];
        yield return [Lasting(F(B(StatsFromValuePassive.BlockId, ("how", "per"), ("amount", "500"), ("value", "its_materials")), "cards", YourField)), "Each monster you control gains 500 ATK for each material attached to it."];
        yield return [Lasting(F(B(StatsFromValuePassive.BlockId, ("dir", "becomes"), ("value", "opp_lp")), "cards", YourField)), "The ATK of monsters you control becomes equal to your opponent's LP."];
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("dir", "becomes"), ("stat", "both"), ("how", "half"), ("value", "your_lp"))), "This card's ATK/DEF become equal to half your LP."];
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("dir", "becomes"), ("stat", "def"), ("how", "double"), ("value", "gained_turn"))), "This card's DEF becomes equal to double the LP you gained this turn."];
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("dir", "lose"), ("stat", "both"), ("how", "per"), ("amount", "100"), ("value", "its_counters"))), "This card loses 100 ATK/DEF for each counter on it."];
        // Levels.
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("stat", "level"), ("value", "its_materials"))), "This card's Level increases by the number of materials attached to it."];
        yield return [Lasting(F(B(StatsFromValuePassive.BlockId, ("stat", "level"), ("dir", "lose"), ("how", "per"), ("amount", "1"), ("value", "count")), ValueSources.CardsKey, YourGy.Clone())), "This card's Level decreases by 1 for each card in your GY."];
        yield return [Lasting(F(F(B(StatsFromValuePassive.BlockId, ("stat", "level"), ("dir", "becomes"), ("value", "count")), ValueSources.CardsKey, YourGy.Clone()), "cards", YourField)), "The Levels of monsters you control become equal to the number of cards in your GY."];
        yield return [Lasting(B(StatsFromValuePassive.BlockId, ("stat", "level"), ("how", "half"), ("value", "damage_turn"))), "This card's Level increases by half the damage you took this turn."];
        // One-off actions.
        yield return [Ignition(F(B(StatsFromValueAction.BlockId, ("dir", "lose"), ("value", "its_orig_atk"), ("target", "true")), "cards", OppFaceUp)),
            "You can target 1 face-up monster your opponent controls; it loses ATK equal to its original ATK until the end of this turn."];
        yield return [Ignition(F(B(StatsFromValueAction.BlockId, ("count", "2"), ("dir", "lose"), ("how", "per"), ("amount", "500"), ("value", "count"), ("target", "true"), ("duration", "always")), "cards", OppFaceUp)),
            "You can target 2 face-up monsters your opponent controls; they lose 500 ATK for each card in your hand."];
        yield return [Ignition(B(StatsFromValueAction.BlockId, ("count", "2"), ("how", "per"), ("amount", "100"), ("value", "its_level"), ("target", "true"))),
            "You can target 2 face-up monsters you control; each of them gains ATK equal to its Level x 100 until the end of this turn."];
        yield return [Ignition(B(StatsFromValueAction.BlockId, ("count", "2"), ("stat", "level"), ("dir", "becomes"), ("value", "its_rank"), ("target", "true"))),
            "You can target 2 face-up monsters you control; the Level of each of them becomes equal to its Rank until the end of this turn."];
        yield return [Ignition(B(StatsFromValueAction.BlockId, ("stat", "level"), ("value", "lp_below"), ("target", "true"), ("duration", "always"))),
            "You can target 1 face-up monster you control; its Level increases by how much lower your LP is than your opponent's."];
        yield return [Ignition(B(StatsFromValueAction.BlockId, ("count", "2"), ("stat", "def"), ("dir", "becomes"), ("value", "sum_level"), ("target", "true"))),
            "You can target 2 face-up monsters you control; their DEF becomes equal to the total Levels of the cards in your hand until the end of this turn."];
        yield return [Ignition(B(StatsFromValueAction.BlockId, ("mode", "all"), ("how", "per"), ("amount", "100"), ("value", "its_level"), ("duration", "always"))),
            "You can activate this effect; each face-up monster you control gains ATK equal to its Level x 100."];
        yield return [Ignition(F(B(StatsFromValueAction.BlockId, ("mode", "all"), ("stat", "def"), ("how", "per"), ("amount", "100"), ("value", "count")), ValueSources.CardsKey, YourGy.Clone())),
            "You can activate this effect; all face-up monsters you control gain 100 DEF for each card in your GY until the end of this turn."];
        yield return [Ignition(B(StatsFromValueAction.BlockId, ("count", "2"), ("dir", "becomes"), ("how", "double"), ("value", "its_orig_def"), ("duration", "always"))),
            "You can activate this effect; the ATK of each of 2 face-up monsters you control becomes equal to double its original DEF."];
        yield return [Ignition(B(StatsFromValueAction.BlockId, ("count", "2"), ("dir", "becomes"), ("value", "opp_lp"), ("duration", "always"))),
            "You can activate this effect; the ATK of 2 face-up monsters you control becomes equal to your opponent's LP."];
        yield return [Ignition(F(F(B(StatsFromValueAction.BlockId, ("how", "per"), ("amount", "500"), ("value", "count")), ValueSources.CardsKey, new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Graveyard }), "cards", new CardFilter { ThisCard = true })),
            "You can activate this effect; this card gains 500 ATK for each card in your opponent's GY until the end of this turn."];
        // Damage and LP.
        yield return [Ignition(F(B(LifeFromValueAction.BlockId, ("how", "per"), ("value", "count")), ValueSources.CardsKey, new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand })),
            "You can inflict 300 damage to your opponent for each card in your opponent's hand."];
        yield return [Ignition(B(LifeFromValueAction.BlockId, ("value", "sum_atk"))), "You can inflict damage to your opponent equal to the total ATK of the cards in your hand."];
        yield return [Ignition(B(LifeFromValueAction.BlockId, ("who", "you"), ("how", "half"), ("value", "opp_lp"))), "You can activate this effect; you take damage equal to half your opponent's LP."];
        yield return [Ignition(B(LifeFromValueAction.BlockId, ("who", "both"), ("how", "per"), ("amount", "200"), ("value", "count"))), "You can activate this effect; both players take 200 damage for each card in your hand."];
        yield return [Ignition(B(LifeFromValueAction.BlockId, ("who", "both"), ("value", "its_orig_atk"))), "You can activate this effect; both players take damage equal to this card's original ATK."];
        yield return [Ignition(B(LifeFromValueAction.BlockId, ("what", "gain"), ("who", "you"), ("how", "per"), ("value", "count"))), "You can gain 300 LP for each card in your hand."];
        yield return [Ignition(B(LifeFromValueAction.BlockId, ("what", "gain"), ("who", "you"), ("how", "half"), ("value", "lp_diff"))), "You can gain LP equal to half the difference between both players' LP."];
        yield return [Ignition(B(LifeFromValueAction.BlockId, ("what", "gain"), ("who", "opponent"), ("how", "per"), ("amount", "100"), ("value", "its_materials"))),
            "You can activate this effect; your opponent gains 100 LP for each material attached to this card."];
        yield return [Ignition(B(LifeFromValueAction.BlockId, ("what", "gain"), ("who", "both"), ("how", "per"), ("amount", "250"), ("value", "its_level"))),
            "You can activate this effect; both players gain LP equal to this card's Level x 250."];
        yield return [new EffectEvent { When = new BlockInstance("when.event").With("event", "you_pay_lp"), Do = [B(LifeFromValueAction.BlockId, ("value", "event"))] },
            "If you pay LP: You can inflict damage to your opponent equal to that amount."];
        yield return [new EffectEvent { When = new BlockInstance("when.event").With("event", "you_damage"), Do = [F(B(StatsFromValueAction.BlockId, ("value", "event"), ("how", "double")), "cards", new CardFilter { ThisCard = true })] },
            "If you take damage: You can activate this effect; this card gains ATK equal to double that amount until the end of this turn."];
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void Text_ReadsLikeACard(EffectEvent effect, string text)
    {
        var p = Mon(Id, null, effect);
        Assert.Equal(text, EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics));
        Assert.Empty(ScriptChecker.Check(r.Lua));
    }

    // ---------------------------------------------------------------- every combination compiles

    /// <summary>The die roll or coin toss a value block's "the result" or "the number of heads" comes from.</summary>
    private static BlockInstance[] Before(string source) => source switch
    {
        "roll" => [B(RollDieAction.BlockId)],
        "heads" => [B(TossCoinAction.BlockId, ("count", "3"))],
        // "That monster's ..." reads the cards the step before affected.
        _ when ValueSources.Affected(source) => [B("do.destroy")],
        _ => [],
    };

    public static IEnumerable<object[]> AllCombinations()
    {
        foreach (var src in ValueSources.Sources.Select(o => o.Value))
        foreach (var how in ValueSources.Hows.Select(o => o.Value))
            yield return [src, how];
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void EveryCombination_Compiles(string source, string how)
    {
        var projects = new List<CardProject>();
        foreach (var dir in ValueSources.Dirs.Select(o => o.Value))
        foreach (var stat in ValueSources.Stats.Select(o => o.Value))
        {
            if (!ValueSources.AtResolution(source))
            {
                projects.Add(Mon(Id, null, Lasting(B(StatsFromValuePassive.BlockId, ("value", source), ("how", how), ("dir", dir), ("stat", stat)))));
                var field = B(StatsFromValuePassive.BlockId, ("value", source), ("how", how), ("dir", dir), ("stat", stat));
                field.Filters["cards"] = OppMonsters.Clone();
                projects.Add(Mon(Id, null, Lasting(field)));
            }
            var when = source == "event" ? new BlockInstance("when.event").With("event", "you_damage") : new BlockInstance("when.ignition");
            foreach (var target in new[] { "false", "true" })
                projects.Add(Mon(Id, null, new EffectEvent { When = when, Do = [.. Before(source), B(StatsFromValueAction.BlockId, ("value", source), ("how", how), ("dir", dir), ("stat", stat), ("target", target))] }));
            projects.Add(Mon(Id, null, new EffectEvent { When = when, Do = [.. Before(source), B(StatsFromValueAction.BlockId, ("value", source), ("how", how), ("dir", dir), ("stat", stat), ("mode", "all"))] }));
        }
        foreach (var what in new[] { "damage", "gain" })
        foreach (var who in new[] { "opponent", "you", "both" })
        {
            var when = source == "event" ? new BlockInstance("when.event").With("event", "you_damage") : new BlockInstance("when.ignition");
            projects.Add(Mon(Id, null, new EffectEvent { When = when, Do = [.. Before(source), B(LifeFromValueAction.BlockId, ("value", source), ("how", how), ("what", what), ("who", who))] }));
        }
        // "The LP paid" is paid by the effect's cost.
        if (source == "lp_paid") foreach (var p in projects) p.Effects[0].Cost.Add(B("cost.pay_lp", ("how", "multiple")));
        foreach (var p in projects)
        {
            var r = ScriptCompiler.Compile(p);
            Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics));
            Assert.Empty(ScriptChecker.Check(r.Lua));
            var text = EffectTextGenerator.Generate(p);
            Assert.DoesNotContain("for each 1 of", text);
            Assert.DoesNotContain("  ", text);
        }
    }
}
