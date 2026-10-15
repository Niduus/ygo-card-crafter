using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

public class CardLibraryTests
{
    private static CardLibrary Library(TempDir tmp)
    {
        var db = Path.Combine(tmp.Path, "cards.cdb");
        CdbFile.WriteFresh(db,
        [
            new CdbRow { Id = 14558127, Type = 0x21, Name = "Ash Blossom & Joyous Spring", Desc = "When a card or effect is activated that includes any of these effects (Quick Effect): You can discard this card; negate that effect." },
            new CdbRow { Id = 55144522, Type = 0x2, Name = "Pot of Greed", Desc = "Draw 2 cards." },
            new CdbRow { Id = 97268402, Type = 0x21, Name = "Effect Veiler", Desc = "During your opponent's Main Phase (Quick Effect): You can send this card from your hand to the GY, then target 1 Effect Monster your opponent controls; negate the effects of that face-up monster." },
            new CdbRow { Id = 12345678, Type = 0x4, Name = "Negate Attack", Desc = "When an opponent's monster declares an attack: Target the attacking monster; negate the attack, then end the Battle Phase." },
            new CdbRow { Id = 12345679, Alias = 12345678, Type = 0x4, Name = "Negate Attack", Desc = "Alternate art." },
        ]);
        return CardLibrary.Load([db], tmp.Path);
    }

    [Fact]
    public void FindsEveryCardWhoseTextSaysAWord()
    {
        using var tmp = new TempDir();
        var lib = Library(tmp);
        Assert.Equal(4, lib.Cards.Count); // the alternate artwork is listed once
        var negate = lib.Search("negate");
        Assert.Equal(["Negate Attack", "Ash Blossom & Joyous Spring", "Effect Veiler"], negate.Select(c => c.Name));
        Assert.Equal(["Pot of Greed"], lib.Search("draw").Select(c => c.Name));
        Assert.Equal(["Effect Veiler"], lib.Search("negate \"face-up monster\"").Select(c => c.Name));
        Assert.Equal(["Negate Attack"], lib.Search("negate", SearchIn.Name).Select(c => c.Name));
        Assert.Equal(2, lib.Search("negate", SearchIn.Text, CardKind.Monster).Count);
        Assert.Empty(lib.Search("pot", SearchIn.Text));
        Assert.Equal("[Trap Card]", lib.Find(12345678)!.TypeLine);
    }

    [Fact]
    public void SplitsAScriptIntoItsEffects_WithTheFunctionsEachNeeds()
    {
        const string lua = """
            --Test
            local s,id=GetID()
            function s.initial_effect(c)
            	c:EnableReviveLimit()
            	--draw
            	local e1=Effect.CreateEffect(c)
            	e1:SetDescription(aux.Stringid(id,0))
            	e1:SetType(EFFECT_TYPE_IGNITION)
            	e1:SetRange(LOCATION_MZONE)
            	e1:SetCountLimit(1,id)
            	e1:SetTarget(s.drtg)
            	e1:SetOperation(s.drop)
            	c:RegisterEffect(e1)
            	--atk
            	local e2=Effect.CreateEffect(c)
            	e2:SetType(EFFECT_TYPE_SINGLE)
            	e2:SetCode(EFFECT_UPDATE_ATTACK)
            	e2:SetValue(s.atkval)
            	c:RegisterEffect(e2)
            	local e3=e2:Clone()
            	e3:SetCode(EFFECT_UPDATE_DEFENSE)
            	c:RegisterEffect(e3)
            end
            s.listed_names={id}
            function s.drtg(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return Duel.IsPlayerCanDraw(tp,1) end
            	Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,1)
            end
            function s.drop(e,tp,eg,ep,ev,re,r,rp)
            	Duel.Draw(tp,s.count(),REASON_EFFECT)
            end
            function s.count()
            	return 1
            end
            function s.atkval(e,c)
            	return 500
            end
            """;
        var pieces = LuaPieces.Split(lua, 123, ["Draw a card"]);
        Assert.Equal(3, pieces.Count);
        Assert.Equal("Revive limit (must be properly Summoned first)", pieces[0].Title);
        Assert.Equal("Draw a card", pieces[1].Title);
        Assert.Equal(3, pieces[1].Functions.Count); // drtg, drop, and count called from drop
        Assert.Equal("Atk", pieces[2].Title);
        Assert.Contains("e3:SetCode(EFFECT_UPDATE_DEFENSE)", pieces[2].Code); // the clone travels with its effect
        Assert.Single(pieces[2].Functions);
    }

    [Fact]
    public void InsertsAPieceIntoAnotherCard_RenamingWhatClashes()
    {
        const string source = """
            function c55144522.initial_effect(c)
            	--Activate
            	local e1=Effect.CreateEffect(c)
            	e1:SetType(EFFECT_TYPE_IGNITION)
            	e1:SetRange(LOCATION_MZONE)
            	e1:SetCountLimit(1,55144522)
            	e1:SetTarget(c55144522.target)
            	e1:SetOperation(c55144522.activate)
            	c:RegisterEffect(e1)
            end
            function c55144522.target(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return Duel.IsPlayerCanDraw(tp,2) end
            end
            function c55144522.activate(e,tp,eg,ep,ev,re,r,rp)
            	Duel.Draw(tp,2,REASON_EFFECT)
            end
            """;
        var piece = LuaPieces.Split(source, 55144522).Single();
        var target = ScriptCompiler.Compile(Samples.DragonCommander()).Lua;
        // Make sure a name clashes, so renaming is exercised.
        target += "function s.target(e,tp,eg,ep,ev,re,r,rp,chk)\n\treturn true\nend\n";
        var merged = LuaPieces.Insert(target, piece);
        Assert.Contains("e1:SetTarget(s.target2)", merged);
        Assert.Contains("function s.target2(", merged);
        Assert.Contains("function s.activate(", merged);
        Assert.Contains("SetCountLimit(1,id)", merged);
        Assert.DoesNotContain("55144522", merged.Replace("--from 55144522", ""));
        Assert.Empty(ScriptChecker.Check(merged));
    }

    [Fact]
    public void RetargetsAWholeScriptToANewPasscode()
    {
        var lua = "function c55144522.initial_effect(c)\n\tlocal e1=Effect.CreateEffect(c)\n\te1:SetCountLimit(1,55144522)\n\te1:SetOperation(c55144522.op)\nend\nfunction c55144522.op(e)\nend\n";
        var moved = LuaPieces.Retarget(lua, 55144522, 300000000);
        Assert.Contains("function c300000000.initial_effect(c)", moved);
        Assert.Contains("SetCountLimit(1,300000000)", moved);
        Assert.Contains("c300000000.op", moved);
        Assert.DoesNotContain("55144522", moved);
    }

    /// <summary>Every official script splits into pieces that each insert into a card and still pass the script checks.</summary>
    [EngineFact]
    public void OfficialScriptsSplitAndInsertCleanly()
    {
        var target = ScriptCompiler.Compile(Samples.DragonCommander()).Lua;
        int scripts = 0, pieces = 0;
        foreach (var file in Directory.EnumerateFiles(EngineScenario.EngineScripts!, "c*.lua").OrderBy(f => f).Take(400))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file)[1..], out var id)) continue;
            var lua = File.ReadAllText(file);
            var split = LuaPieces.Split(lua, id);
            if (split.Count == 0) continue;
            scripts++;
            foreach (var piece in split)
            {
                var merged = LuaPieces.Insert(target, piece);
                var syntax = ScriptChecker.Check(merged).Where(d => d.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert.True(syntax.Count == 0, $"{Path.GetFileName(file)} [{piece.Title}]: {string.Join("; ", syntax)}\n{merged}");
                pieces++;
            }
        }
        Assert.True(scripts > 300 && pieces > scripts, $"{scripts} scripts, {pieces} pieces");
    }

    /// <summary>Pot of Greed's effect, taken out of its script and put on a new card, draws 2 in a real duel.</summary>
    [EngineFact]
    public void APieceOfPotOfGreed_WorksOnANewCard()
    {
        var pot = File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, "c55144522.lua"));
        var piece = LuaPieces.Split(pot, 55144522).Single();
        var card = new CardProject { Card = new CardData { Id = 100000900, Name = "Greedy Copy", Kind = CardKind.Spell } };
        // A Spell with no effects of its own, then Pot of Greed's activation added to it.
        var empty = "local s,id=c100000900,100000900\nfunction s.initial_effect(c)\nend\n";
        card.UseCustomLua = true;
        card.CustomLua = LuaPieces.Insert(empty, piece);
        new EngineScenario(card)
            .Card(100000900, 0, "hand").Card(Warrior, 0, "deck", 10)
            .Activate(100000900)
            .ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2")
            .AssertPasses();
    }
}
