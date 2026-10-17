using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official scripts read into the new picker pieces: "1 "Ancient Gear" monster or 1 "Geartown"" (two kinds of cards),
/// "whose ATK equals its own DEF" (aux.AtkEqualsDef), and Crowley's name as Fusion Material.
/// </summary>
public class PickerTranslationTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static CardData Monster(long id, MonsterFrame frame = MonsterFrame.Effect) => new()
    {
        Id = id, Name = "Translated " + id, Frame = frame, Level = 4, Atk = 1000, Def = 1000, Attribute = CardAttribute.Earth, Race = CardRace.Machine,
        LinkMarkers = frame == MonsterFrame.Link ? LinkMarker.Bottom | LinkMarker.Top : LinkMarker.None,
    };

    private static EffectEvent Piece(long id, CardData card, Func<EffectEvent, bool> which)
    {
        var result = ScriptTranslator.Translate(Script(id), id, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(which);
        Assert.True(effect is not null, string.Join("\n", result.Pieces.Select(p => $"{p.Piece?.Title}: {p.Reason}")));
        var compiled = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect!] });
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        return effect!;
    }

    /// <summary>Ancient Gear Ballista: "add 1 "Ancient Gear" monster or 1 "Geartown" from your Deck to your hand".</summary>
    [EngineFact]
    public void AncientGearBallista_OrAnotherKind()
    {
        var effect = Piece(10547580, Monster(10547580, MonsterFrame.Link), e => e.Do.Any(b => b.BlockId == "do.add_to_hand"));
        var f = effect.Do.Single(b => b.BlockId == "do.add_to_hand").Filters["cards"];
        Assert.Equal((0x7, FilterCardType.Monster), (f.Archetype, f.CardType));
        Assert.Equal(37694547, f.OrCards!.CardCode);
        Assert.Contains("1 \"0x7\" monster or 1", EffectTextGenerator.Generate(new CardProject { Card = Monster(10547580, MonsterFrame.Link), Effects = [effect] }));
    }

    /// <summary>Gizmek Taniguku: "1 Machine monster whose ATK equals its own DEF".</summary>
    [EngineFact]
    public void GizmekTaniguku_AtkEqualsDef()
    {
        var effect = Piece(23384666, Monster(23384666), e => e.Do.Any(b => b.BlockId == "do.special_summon"));
        var f = effect.Do.Single(b => b.BlockId == "do.special_summon").Filters["cards"];
        Assert.Contains(f.Requirements, r => r.Property == "atk_eq_def" && r.Op == RequirementOp.Is);
    }

    /// <summary>Crowley, the Gifted Magistus: "If this face-up card is used as Fusion Material, its name can be treated as "Aleister the Invoker"".</summary>
    [EngineFact]
    public void Crowley_NameAsFusionMaterial()
    {
        var effect = Piece(875572, Monster(875572), e => e.Do.Any(b => b.BlockId == MaterialTreatedAsPassive.BlockId));
        var b = effect.Do.Single();
        Assert.Equal(("fusion_code", "field"), (b.Values["kind"], b.Values["where"]));
        Assert.Equal(86120751, b.Filters["name"].CardCode);
    }
}
