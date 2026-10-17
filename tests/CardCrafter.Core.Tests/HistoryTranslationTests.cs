using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official shapes of the history conditions, trigger details and limits, read into blocks: "Up to twice per turn",
/// "except by drawing it", the flags kept for "this card destroyed a monster by battle" and "this card was activated
/// this turn", "this card is already face-up" and Triple Tactics Talent's Main Phase activity counter.
/// </summary>
public class HistoryTranslationTests
{
    private const long Salamandra = 4058065, GearBox = 60953949, Diablosis = 95474755, DarkCity = 4663194, Preparations = 21639276, Tactics = 25311006, Baronne = 84815190;

    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScripts!, $"c{id}.lua"));

    private static List<TranslatedPiece> Pieces(long id, CardData card) => ScriptTranslator.Translate(Script(id), id, card).Pieces;

    private static CardData Monster(long id, string name) => new() { Id = id, Name = name, Frame = MonsterFrame.Effect };

    [EngineFact]
    public void Salamandra_UpToTwicePerTurn()
    {
        var effect = Assert.Single(Pieces(Salamandra, Monster(Salamandra, "Evilswarm Salamandra")).SelectMany(p => p.Effects));
        Assert.Equal(OncePerTurn.Soft, effect.Limit);
        Assert.Equal(2, effect.Times);
    }

    [EngineFact]
    public void GearBox_ExceptByDrawingIt()
    {
        var effect = Assert.Single(Pieces(GearBox, Monster(GearBox, "Ancient Gear Box")).SelectMany(p => p.Effects));
        Assert.Equal(ThisCardDetailTrigger.BlockId, effect.When!.BlockId);
        Assert.Equal("to_hand", effect.When.Values["event"]);
        Assert.Equal("deck_gy", effect.When.Values["from"]);
        Assert.Equal("not_draw", effect.When.Values["by"]);
    }

    [EngineFact]
    public void Diablosis_TheBattleFlagIsTheIfBlocks()
    {
        var pieces = Pieces(Diablosis, new CardData { Id = Diablosis, Name = "Number 89: Diablosis the Mind Hacker", Frame = MonsterFrame.Xyz });
        // The watcher on its battles needs no block; the GY banish's condition is read (its target step isn't yet).
        Assert.DoesNotContain(pieces, p => p.Reason is { } r && r.Contains("EVENT_BATTLE_DESTROYING", StringComparison.Ordinal));
        Assert.DoesNotContain(pieces, p => p.Reason is { } r && r.Contains("GetFlagEffect", StringComparison.Ordinal));
    }

    [EngineFact]
    public void DarkCity_TheActivationFlagIsTheIfBlock()
    {
        var pieces = Pieces(DarkCity, new CardData { Id = DarkCity, Name = "Dark City", Kind = CardKind.Spell, SpellKind = SpellKind.Field });
        // The activation with only the flag in its cost is the card's own; its search's condition is "this card was activated this turn".
        Assert.DoesNotContain(pieces, p => p.Reason is { } r && r.Contains("GetFlagEffect", StringComparison.Ordinal));
        Assert.True(pieces.Single(p => p.Piece.Code.Contains("EFFECT_TYPE_ACTIVATE", StringComparison.Ordinal)).Ignored);
    }

    [EngineFact]
    public void Preparations_AlreadyFaceUp()
    {
        var pieces = Pieces(Preparations, new CardData { Id = Preparations, Name = "Kashtira Preparations", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous });
        Assert.DoesNotContain(pieces, p => p.Reason is { } r && r.Contains("STATUS_EFFECT_ENABLED", StringComparison.Ordinal));
    }

    [EngineFact]
    public void Tactics_TheMainPhaseCounterIsTheCondition()
    {
        var pieces = Pieces(Tactics, new CardData { Id = Tactics, Name = "Triple Tactics Talent", Kind = CardKind.Spell });
        Assert.DoesNotContain(pieces, p => p.Reason is { } r && r.Contains("GetCustomActivityCount", StringComparison.Ordinal));
    }

    [EngineFact]
    public void Baronne_OnceWhileFaceUpAndOncePerTurn()
    {
        var pieces = Pieces(Baronne, new CardData { Id = Baronne, Name = "Baronne de Fleur", Frame = MonsterFrame.Synchro });
        Assert.DoesNotContain(pieces, p => p.Reason is { } r && r.Contains("more than once", StringComparison.Ordinal));
    }
}
