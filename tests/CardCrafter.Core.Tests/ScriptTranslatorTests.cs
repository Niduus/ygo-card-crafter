using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Official scripts translated into blocks must behave like the originals.</summary>
public class ScriptTranslatorTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static CardProject Translated(long id, CardData card)
    {
        var result = ScriptTranslator.Translate(Script(id), id, card);
        Assert.True(result.Complete, string.Join("\n", result.Pieces.Where(p => !p.Translated).Select(p => $"{p.Piece?.Title}: {p.Reason}")));
        var project = new CardProject { Card = card, Effects = result.Pieces.Where(p => p.Effect is not null).Select(p => p.Effect!).ToList() };
        var compiled = ScriptCompiler.Compile(project);
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        return project;
    }

    private static CardData SpellCard(long id, SpellKind kind = SpellKind.Normal) => new() { Id = id, Name = "Translated", Kind = CardKind.Spell, SpellKind = kind };

    [EngineFact]
    public void PotOfGreed_Draws2()
    {
        var p = Translated(55144522, SpellCard(100001000));
        Assert.Equal("do.draw", p.Effects.Single().Do.Single().BlockId);
        new EngineScenario(p).Card(100001000, 0, "hand").Card(Warrior, 0, "deck", 10).Activate(100001000).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2").AssertPasses();
    }

    [EngineFact]
    public void Ookazi_Burns800()
    {
        var p = Translated(19523799, SpellCard(100001010));
        new EngineScenario(p).Card(100001010, 0, "hand").Activate(100001010).ExpectActivations(1).Check("Duel.GetLP(1)==7200").AssertPasses();
    }

    [EngineFact]
    public void UpstartGoblin_DrawsThenOpponentGains1000()
    {
        var p = Translated(70368879, SpellCard(100001020));
        Assert.Equal(ActionLink.Then, p.Effects.Single().Do[1].Link);
        new EngineScenario(p).Card(100001020, 0, "hand").Card(Warrior, 0, "deck", 10).Activate(100001020).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").Check("Duel.GetLP(1)==9000").AssertPasses();
    }

    [EngineFact]
    public void ReinforcementOfTheArmy_SearchesALevel4Warrior()
    {
        var p = Translated(32807846, SpellCard(100001030));
        var filter = p.Effects.Single().Do.Single().Filters["cards"];
        Assert.Equal(CardRace.Warrior, filter.Race);
        Assert.Equal(4, filter.LevelMax);
        new EngineScenario(p).Card(100001030, 0, "hand").Card(Dragon, 0, "deck", 3).Card(Warrior, 0, "deck", 3).Activate(100001030).ExpectActivations(1)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,1001)")
            .Check("not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,1002)").AssertPasses();
    }

    [EngineFact]
    public void MonsterReborn_RevivesFromEitherGY()
    {
        var p = Translated(83764718, SpellCard(100001040));
        new EngineScenario(p).Card(100001040, 0, "hand").Card(Warrior, 1, "grave").Activate(100001040).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").AssertPasses();
    }

    [EngineFact]
    public void MysticalSpaceTyphoon_DestroysASpellOrTrap_NotAMonster()
    {
        var p = Translated(5318639, SpellCard(100001050, SpellKind.QuickPlay));
        new EngineScenario(p).Card(100001050, 0, "hand").Card(Spell, 1, "szone").Card(Fiend, 1, "mzone").Activate(100001050).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_SZONE,0)==0").Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1").AssertPasses();
    }

    [EngineFact]
    public void Sangan_StaysScript_WithAReason()
    {
        // Sangan also forbids activating the searched card this turn, which the blocks can't say: it stays partly script.
        var result = ScriptTranslator.Translate(Script(26202165), 26202165, new CardData { Id = 100001060, Name = "Translated", Level = 3, Atk = 1000, Def = 600 });
        Assert.False(result.Complete);
        var piece = result.Pieces.Single();
        Assert.False(piece.Translated);
        Assert.False(string.IsNullOrWhiteSpace(piece.Reason));
    }

    /// <summary>Across every official script: what translates compiles cleanly, and a useful share translates.</summary>
    [EngineFact]
    public void TranslationsOfOfficialScriptsCompile()
    {
        int scripts = 0, complete = 0, pieces = 0, translated = 0;
        var reasons = new Dictionary<string, int>();
        var details = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(EngineScenario.EngineScripts!, "c*.lua"))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file)[1..], out var id)) continue;
            var card = new CardData { Id = 100002000, Name = "T", Level = 4 };
            var result = ScriptTranslator.Translate(File.ReadAllText(file), id, card);
            if (result.Pieces.Count == 0) continue;
            scripts++;
            pieces += result.Pieces.Count;
            translated += result.TranslatedCount;
            foreach (var piece in result.Pieces.Where(p => p.Effect is not null))
            {
                // Each translated effect compiles to a valid script on a card of a fitting kind.
                foreach (var kind in new[] { CardKind.Monster, CardKind.Spell, CardKind.Trap })
                {
                    var p = new CardProject { Card = new CardData { Id = 100002000, Name = "T", Level = 4, Kind = kind }, Effects = [piece.Effect!] };
                    var r = ScriptCompiler.Compile(p);
                    if (r.HasErrors) continue;
                    Assert.DoesNotContain(ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
                    break;
                }
            }
            if (result.Complete) complete++;
            foreach (var r in result.Pieces.Where(p => !p.Translated))
            {
                var key = System.Text.RegularExpressions.Regex.Replace(r.Reason ?? "", @"\(.*", "(…");
                reasons[key] = reasons.GetValueOrDefault(key) + 1;
                var detail = System.Text.RegularExpressions.Regex.Replace(r.Reason ?? "", @"\d{4,}", "N");
                details[detail] = details.GetValueOrDefault(detail) + 1;
            }
        }
        Console.WriteLine($"{complete} of {scripts} official scripts translate completely; {translated} of {pieces} effects.");
        if (Environment.GetEnvironmentVariable("CARDCRAFTER_TRANSLATION_REPORT") is { } report)
        {
            File.WriteAllLines(report, reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Value}\t{r.Key}"));
            File.WriteAllLines(Path.ChangeExtension(report, ".detail.txt"), details.OrderByDescending(r => r.Value).Take(400).Select(r => $"{r.Value}\t{r.Key}"));
        }
        Assert.True(complete > 250, $"{complete} of {scripts}");
    }
}

public class TranslatorRobustnessTests
{
    /// <summary>Every official script: translating, describing each effect and switching a card to blocks never throws.</summary>
    [EngineFact]
    public void EveryOfficialScript_TranslatesDescribesAndApplies_WithoutThrowing()
    {
        foreach (var file in Directory.EnumerateFiles(EngineScenario.EngineScripts!, "c*.lua"))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file)[1..], out var id)) continue;
            var lua = File.ReadAllText(file);
            foreach (var kind in new[] { CardKind.Monster, CardKind.Spell })
            {
                var card = new CardData { Id = id, Name = "T", Level = 4, Kind = kind };
                try
                {
                    var result = ScriptTranslator.Translate(lua, id, card);
                    foreach (var p in result.Pieces)
                    {
                        if (p.Effect is { } e) CardCrafter.Core.Text.EffectTextGenerator.Describe(e, card);
                        _ = p.Piece.Title;
                    }
                    _ = ScriptTranslator.Untranslated(result).ToList();
                    var project = new CardProject { Card = card, UseCustomLua = true, CustomLua = lua };
                    ScriptTranslator.ApplyTo(project, null, out _);
                    CardCrafter.Core.Text.EffectTextGenerator.Generate(project);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"c{id}.lua as {kind}: {ex}");
                }
            }
        }
    }
}
