using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Validation;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Compiles every block with every choice and flag value, and checks each script parses as Lua 5.4
/// and only uses functions and constants that exist in the engine.
/// </summary>
public class AllBlocksTests
{
    public static IEnumerable<object[]> Variants()
    {
        foreach (var def in BlockRegistry.All)
            foreach (var (label, inst) in VariantsOf(def))
                yield return [def.Id, label, inst];
    }

    private static IEnumerable<(string, BlockInstance)> VariantsOf(BlockDefinition def)
    {
        yield return ("defaults", def.CreateInstance());
        foreach (var p in def.Params)
        {
            switch (p.Type)
            {
                case ParamType.Choice:
                    foreach (var c in p.Choices.Skip(1))
                    {
                        var inst = def.CreateInstance().With(p.Key, c.Value);
                        if (def.Id == "cost.move_cards" && c.Value == "grave") inst.Filters["cards"].Zones = Zone.Hand;
                        yield return ($"{p.Key}={c.Value}", inst);
                    }
                    break;
                case ParamType.Bool:
                    var flipped = def.CreateInstance().With(p.Key, p.Default == "true" ? "false" : "true");
                    // Targeting needs a public zone to aim at.
                    if (p.Key == "target" && flipped.Filters.TryGetValue("cards", out var f) && (f.Zones & (Zone.Deck | Zone.Hand | Zone.ExtraDeck)) != Zone.None)
                        f.Zones = Zone.Graveyard;
                    yield return ($"{p.Key}=flipped", flipped);
                    break;
                case ParamType.Integer:
                    if (p.Max > 1) yield return ($"{p.Key}=2", def.CreateInstance().With(p.Key, Math.Max(2, p.Min)));
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void BlockVariantCompilesToValidLua(string blockId, string variant, BlockInstance inst)
    {
        var def = BlockRegistry.Find(blockId)!;
        var project = ProjectFor(def, inst);
        var result = ScriptCompiler.Compile(project, Pack(project));

        var errors = result.Diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        Assert.True(errors.Count == 0, $"{blockId} [{variant}]: {string.Join("; ", errors)}\n{result.Lua}");
        var checks = ScriptChecker.Check(result.Lua);
        Assert.True(checks.Count == 0, $"{blockId} [{variant}]: {string.Join("; ", checks)}\n{result.Lua}");
    }

    private const long TokenId = 100000099;

    /// <summary>The pack the card is compiled in, with a Token card for "Special Summon Tokens".</summary>
    private static CardPack Pack(CardProject project) => new()
    {
        Cards =
        [
            project,
            new CardProject { Card = new CardData { Id = TokenId, Name = "Sheep Token", Frame = MonsterFrame.Token, Level = 1, Race = CardRace.Beast, Attribute = CardAttribute.Earth } },
        ],
    };

    /// <summary>Wraps a single block in the smallest effect that makes sense for it.</summary>
    /// <summary>Blocks about Link Arrows need a Link monster to sit on.</summary>
    private static bool NeedsLink(BlockDefinition def, BlockInstance inst) =>
        def.Id is "if.points_to" || inst.Values.GetValueOrDefault("zone") == "pointed";

    private static CardProject ProjectFor(BlockDefinition def, BlockInstance inst)
    {
        var project = ProjectFor0(def, inst);
        if (NeedsLink(def, inst))
        {
            project.Card.Frame = MonsterFrame.Link;
            project.Card.LinkMarkers = LinkMarker.Left | LinkMarker.Right;
            project.Procedure.Kind = SummonProcedureKind.Link;
        }
        if (def.Id == "do.negate") project.Effects[0].When = new BlockInstance("when.quick").With("timing", "chain_any");
        switch (def.Id)
        {
            case "do.negate_summon":
                project.Effects[0].When = new BlockInstance("when.quick").With("timing", "would_summon");
                break;
            case "do.attach":
                project.Card.Frame = MonsterFrame.Xyz;
                project.Procedure.Kind = SummonProcedureKind.Xyz;
                break;
            case "passive.equipped":
                project.Card.Kind = CardKind.Spell;
                project.Card.SpellKind = SpellKind.Equip;
                break;
            case "do.tokens":
                inst.Values["token"] = TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
        }
        // A card Special Summons itself from where it is, never from the field.
        if (def.Id == "do.special_summon_self") project.Effects[0].When = new BlockInstance("when.ignition").With("zone", "hand");
        return project;
    }

    private static CardProject ProjectFor0(BlockDefinition def, BlockInstance inst)
    {
        var effect = new EffectEvent { Label = def.Name };
        CardData card;
        switch (def.Kind)
        {
            case BlockKind.Trigger:
                card = Samples.CardFor(def);
                effect.When = inst;
                if (def.Id != SummonProcedureTrigger.BlockId)
                    effect.Do.Add(def.Id == "when.while_face_up" ? new BlockInstance("passive.untargetable") : new BlockInstance("do.draw"));
                break;
            case BlockKind.Passive:
                card = new CardData { Id = 100000013, Name = "Test" };
                effect.When = new BlockInstance("when.while_face_up");
                effect.Do.Add(inst);
                break;
            default:
                card = new CardData { Id = 100000014, Name = "Test" };
                effect.When = new BlockInstance("when.ignition");
                if (def.Kind == BlockKind.Condition) effect.If.Add(inst);
                if (def.Kind == BlockKind.Cost) effect.Cost.Add(inst);
                if (def.Kind == BlockKind.Action) effect.Do.Add(inst);
                else effect.Do.Add(new BlockInstance("do.draw"));
                break;
        }
        return new CardProject { Card = card, Effects = [effect] };
    }

    [Fact]
    public void EveryBlockHasUniqueIdAndValidSentence()
    {
        Assert.Equal(BlockRegistry.All.Count, BlockRegistry.All.Select(b => b.Id).Distinct().Count());
        foreach (var def in BlockRegistry.All)
        {
            var keys = SentenceParser.Parse(def.Sentence).Where(s => s.Key is not null).Select(s => s.Key!).ToList();
            foreach (var k in keys) Assert.True(def.Param(k) is not null, $"{def.Id}: sentence blank {{{k}}} has no parameter");
            foreach (var p in def.Params) Assert.True(keys.Contains(p.Key) || p.Type == ParamType.Bool, $"{def.Id}: parameter {p.Key} is not in the sentence");
        }
    }
}
