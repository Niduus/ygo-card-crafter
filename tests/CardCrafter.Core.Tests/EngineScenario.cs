using System.Diagnostics;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Runs only when the engine harness is built (see tools/engine-harness). Set CARDCRAFTER_HARNESS to the
/// harness binary and CARDCRAFTER_ENGINE_SCRIPTS to a ygopro-scripts checkout (for constant.lua etc).
/// </summary>
public sealed class EngineFactAttribute : FactAttribute
{
    public EngineFactAttribute()
    {
        if (!EngineScenario.Available) Skip = "Engine harness not built (set CARDCRAFTER_HARNESS and CARDCRAFTER_ENGINE_SCRIPTS).";
    }
}

public sealed class EngineTheoryAttribute : TheoryAttribute
{
    public EngineTheoryAttribute()
    {
        if (!EngineScenario.Available) Skip = "Engine harness not built (set CARDCRAFTER_HARNESS and CARDCRAFTER_ENGINE_SCRIPTS).";
    }
}

/// <summary>Builds a duel setup, exports the cards, runs it through the real engine and returns the log.</summary>
public sealed class EngineScenario
{
    public static string? Harness => Environment.GetEnvironmentVariable("CARDCRAFTER_HARNESS");
    public static string? EngineScripts => Environment.GetEnvironmentVariable("CARDCRAFTER_ENGINE_SCRIPTS");
    public static bool Available => File.Exists(Harness) && Directory.Exists(EngineScripts);

    // Vanilla filler cards with no script.
    public const long Warrior = 1001, Dragon = 1002, Fiend = 1004, Spell = 1003, EffectFiller = 1005, Trap = 1006;

    private readonly CardPack _pack = new() { Name = "Engine Test" };
    private readonly List<string> _lines = [];

    public EngineScenario(params CardProject[] cards)
    {
        _pack.Cards.AddRange(cards);
        _pack.Cards.Add(Vanilla(Warrior, "Filler Warrior", CardRace.Warrior, CardAttribute.Earth, 1500));
        _pack.Cards.Add(Vanilla(Dragon, "Filler Dragon", CardRace.Dragon, CardAttribute.Light, 1600));
        _pack.Cards.Add(Vanilla(Fiend, "Filler Fiend", CardRace.Fiend, CardAttribute.Dark, 1000));
        _pack.Cards.Add(new CardProject { Card = new CardData { Id = Spell, Name = "Filler Spell", Kind = CardKind.Spell } });
        _pack.Cards.Add(new CardProject { Card = new CardData { Id = EffectFiller, Name = "Filler Effect Monster", Frame = MonsterFrame.Effect, Level = 4, Atk = 1400, Def = 1000 } });
        _pack.Cards.Add(new CardProject { Card = new CardData { Id = Trap, Name = "Filler Trap", Kind = CardKind.Trap } });
    }

    private static CardProject Vanilla(long id, string name, CardRace race, CardAttribute attr, int atk) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Normal, Race = race, Attribute = attr, Level = 4, Atk = atk, Def = 1000 },
    };

    public EngineScenario Card(long code, int player, string location, int count = 1) { _lines.Add($"card {code} {player} {location} {count}"); return this; }
    public EngineScenario Activate(long code, int times = 1) { _lines.Add($"activate {code} {times}"); return this; }
    public EngineScenario Summon(long code) { _lines.Add($"summon {code}"); return this; }
    public EngineScenario SpSummon(long code) { _lines.Add($"spsummon {code}"); return this; }
    public EngineScenario Announce(long code) { _lines.Add($"announce {code}"); return this; }
    public EngineScenario Turns(int n) { _lines.Add($"turns {n}"); return this; }
    public EngineScenario Check(string lua) { _lines.Add($"check {lua}"); return this; }
    public EngineScenario ExpectActivations(int n) { _lines.Add($"expect_activations {n}"); return this; }

    public (int ExitCode, string Log) Run()
    {
        using var tmp = new TempDir();
        var export = PackExporter.Export(_pack, Path.Combine(tmp.Path, "export"));
        var skipped = export.Cards.Where(c => c.Skipped && c.Card.Effects.Count > 0).ToList();
        if (skipped.Count > 0)
            return (99, "Cards were not exported: " + string.Join("; ", skipped.SelectMany(c => c.Compile.Diagnostics.Concat(c.Checks))));

        var scenario = Path.Combine(tmp.Path, "scenario.txt");
        File.WriteAllLines(scenario, [$"cdb {export.DatabasePath}", .. _lines]);
        var psi = new ProcessStartInfo(Harness!) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(scenario);
        psi.ArgumentList.Add(Path.Combine(export.OutputFolder, "script"));
        psi.ArgumentList.Add(EngineScripts!);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            p.Kill();
            return (98, "Harness timed out.\n" + stdout.Result);
        }
        var scripts = string.Join("\n\n", export.Cards.Where(c => c.ScriptPath is not null && c.Card.Effects.Count > 0).Select(c => File.ReadAllText(c.ScriptPath!)));
        return (p.ExitCode, stdout.Result + stderr.Result + "\n---- scripts\n" + scripts);
    }

    public void AssertPasses()
    {
        var (code, log) = Run();
        Assert.True(code == 0, log);
    }
}
