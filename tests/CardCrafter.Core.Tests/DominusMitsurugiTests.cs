using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using Microsoft.Data.Sqlite;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Dominus (activated from the hand, then locked out of Attributes for the rest of the Duel), Mitsurugi ("Normal or
/// Special Summoned, or Tributed"), Murakumo ("your opponent can discard 1 card, or that effect is negated") and "or when".
/// </summary>
public class DominusMitsurugiTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance F(BlockInstance b, CardFilter f, string key = "cards") { b.Filters[key] = f; return b; }

    private static string? Script(long id) =>
        EngineScenario.EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    private static TranslationResult Translate(long id, CardData card) =>
        ScriptTranslator.Translate(Script(id)!, id, card);

    private static CardData Trap(string name) => new() { Id = 100010000, Name = name, Kind = CardKind.Trap };

    [EngineTheory]
    [InlineData(6325660L, "Target 1 monster your opponent controls; banish it, then if you have no Traps in your GY, your opponent can Special Summon 1 monster from their hand.", "EARTH, WATER, FIRE, and WIND")]
    [InlineData(40366667L, "When a card or effect is activated that includes an effect that Special Summons a monster(s): Negate that effect, then if you have a Trap in your GY, destroy that card.", "LIGHT, EARTH, and WIND")]
    [InlineData(42091632L, "your opponent can Special Summon 1 monster from their GY.", "LIGHT or DARK")]
    [InlineData(97045737L, "When a card or effect is activated that includes an effect that adds a card(s) from the Deck to the hand: Negate that effect", "DARK, WATER, and FIRE")]
    public void DominusTraps_TranslateCompletely(long id, string text, string attributes)
    {
        if (Script(id) is null) return;
        var card = Trap("Dominus");
        var result = Translate(id, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var project = new CardProject { Card = card, Effects = result.Pieces.SelectMany(p => p.Effects).ToList() };
        var generated = EffectTextGenerator.Generate(project);
        Assert.Contains(text, generated);
        Assert.Contains($"If you activated this card from your hand, you cannot activate the effects of {attributes} monsters for the rest of this Duel.", generated);
        Assert.Contains("you can activate this card from your hand", generated);
        var compiled = ScriptCompiler.Compile(project);
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(CardCrafter.Core.Validation.ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        Assert.Contains("EFFECT_TRAP_ACT_IN_HAND", compiled.Lua);
        Assert.Contains("STATUS_ACT_FROM_HAND", compiled.Lua);
    }

    [EngineFact]
    public void DominusLock_FromTheHand_StopsThoseMonstersForTheRestOfTheDuel()
    {
        // A Trap activated from the hand: draw 1, then "you cannot activate the effects of LIGHT monsters for the rest of this Duel".
        var dominus = new CardProject
        {
            Card = new CardData { Id = 100010010, Name = "Lock Trap", Kind = CardKind.Trap },
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.activate"),
                    Do =
                    [
                        B("do.draw"),
                        F(B("do.summon_restriction", ("duration", "duel"), ("what", "activate_monster_of"), ("from_hand", "true")),
                            new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Light }),
                    ],
                },
                new EffectEvent { When = B("when.while_face_up"), Do = [B(ActivateFromHandPassive.BlockId)] },
            ],
        };
        var light = new CardProject
        {
            Card = new CardData { Id = 100010011, Name = "Light Drawer", Level = 4, Attribute = CardAttribute.Light },
            Effects = [new EffectEvent { When = B("when.ignition"), Do = [B("do.draw")] }],
        };
        new EngineScenario(dominus, light).Card(dominus.Card.Id, 0, "hand").Card(light.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 6).Card(Warrior, 1, "deck", 6)
            .Activate(dominus.Card.Id).Activate(light.Card.Id, 3).Turns(3).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{dominus.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void Mitsurugi_SummonedOrTributed_IsOneEffect()
    {
        var card = new CardData { Id = 100010020, Name = "Saji", Level = 4, Race = CardRace.Reptile };
        var result = Translate(18176525, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var search = result.Pieces.SelectMany(p => p.Effects).First();
        // One WHEN per event: Normal Summoned, or Special Summoned, or Tributed.
        Assert.Equal(["when.summoned:normal", "when.summoned:special", "when.this_card:tributed"],
            new[] { search.When! }.Concat(search.AlsoWhen).Select(w => $"{w.BlockId}:{w.Values.GetValueOrDefault("how") ?? w.Values["event"]}"));
        var text = EffectTextGenerator.Describe(search, card);
        Assert.StartsWith("If this card is Normal or Special Summoned, or if this card is Tributed: You can add 1", text);
        var lua = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [search] }).Lua;
        Assert.Contains("EVENT_SUMMON_SUCCESS", lua);
        Assert.Contains("SetCode(EVENT_SPSUMMON_SUCCESS)", lua);
        Assert.Contains("SetCode(EVENT_RELEASE)", lua);
    }

    [EngineFact]
    public void Murakumo_DiscardOrNegate_Translates()
    {
        var card = new CardData { Id = 100010030, Name = "Murakumo", Level = 10, Race = CardRace.Reptile, Frame = MonsterFrame.Ritual };
        var result = Translate(19899073, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var text = EffectTextGenerator.Generate(new CardProject { Card = card, Effects = result.Pieces.SelectMany(p => p.Effects).ToList() });
        Assert.Contains("If this card is Special Summoned: You can destroy all monsters your opponent controls.", text);
        Assert.Contains("When your opponent activates a card or effect (Quick Effect): You can activate this effect; your opponent can discard 1 card, or that effect is negated.", text);
        Assert.Contains("If this card is Tributed: You can add 1", text);
        Assert.Contains("then you can Special Summon this card.", text);
    }

    [EngineFact]
    public void Rosepamela_TranslatesCompletely()
    {
        var card = new CardData { Id = 100010040, Name = "Rosepamela", Level = 4 };
        var result = Translate(65302903, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var effects = result.Pieces.SelectMany(p => p.Effects).ToList();
        var text = EffectTextGenerator.Generate(new CardProject { Card = card, Effects = effects });
        Assert.Contains("You can reveal this card in your hand; add 1", text);
        Assert.Contains("then discard 1 card.", text);
        Assert.Contains("\"0x1ea\" or \"0x31c6\" Trap from your Deck or GY. It can be activated this turn.", text);
        Assert.Contains("Face-down", text);
        var lua = ScriptCompiler.Compile(new CardProject { Card = card, Effects = effects }).Lua;
        Assert.Contains("(c:IsSetCard(0x1ea) or c:IsSetCard(0x31c6))", lua);
        Assert.Contains("EFFECT_TRAP_ACT_IN_SET_TURN", lua);
        Assert.Contains("c:IsFacedown()", lua);
    }

    [Fact]
    public void OrWhen_ClonesTheEffect_AndReadsAsOne()
    {
        var card = new CardData { Id = 100010050, Name = "Either", Level = 4 };
        var effect = new EffectEvent
        {
            When = B("when.summoned", ("how", "normal")),
            AlsoWhen = [B("when.this_card", ("event", "tributed"))],
            Limit = OncePerTurn.Hard,
            Do = [B("do.draw")],
        };
        var p = new CardProject { Card = card, Effects = [effect] };
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.Message)));
        Assert.Contains("SetCode(EVENT_RELEASE)", r.Lua);
        Assert.StartsWith("If this card is Normal Summoned, or if this card is Tributed: You can draw 1 card.", EffectTextGenerator.Generate(p));
    }

    [Fact]
    public void OrWhen_RefusesTriggersThatCannotShareAnEffect()
    {
        var effect = new EffectEvent { When = B("when.summoned", ("how", "normal")), AlsoWhen = [B("when.ignition")], Do = [B("do.draw")] };
        var r = ScriptCompiler.Compile(new CardProject { Card = new CardData { Id = 100010051, Name = "X", Level = 4 }, Effects = [effect] });
        Assert.True(r.HasErrors);
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("Or when", StringComparison.Ordinal));
    }

    [Fact]
    public void OpponentSpecialSummons_LetsThemChoose()
    {
        var effect = new EffectEvent { When = B("when.ignition"), Do = [B("do.opponent_special_summons")] };
        var p = new CardProject { Card = new CardData { Id = 100010060, Name = "X", Level = 4 }, Effects = [effect] };
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Duel.SelectYesNo(1-tp,", r.Lua);
        Assert.Contains("Duel.SpecialSummon(", r.Lua);
        Assert.Contains(",0,1-tp,1-tp,false,false,POS_FACEUP)", r.Lua);
        Assert.Contains("your opponent can Special Summon 1 monster from their hand", EffectTextGenerator.Generate(p));
    }

    [Fact]
    public void TwoArchetypes_AndFaceDown_ReadAndCompile()
    {
        var f = new CardFilter { Owner = CardOwner.You, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Trap, Archetype = 0x1ea, Archetype2 = 0x31c6, FaceDownOnly = true };
        var pack = new CardPack { Archetypes = { new Archetype { Code = 0x1ea, Name = "Dominators" }, new Archetype { Code = 0x31c6, Name = "Dominus Purge" } } };
        Assert.Equal("face-down \"Dominators\" or \"Dominus Purge\" Trap you control", FilterText.Describe(f, pack));
        var parts = FilterCompiler.Conditions(f);
        Assert.Contains("(c:IsSetCard(0x1ea) or c:IsSetCard(0x31c6))", parts);
        Assert.Contains("c:IsFacedown()", parts);
    }

    [Fact]
    public void CardNames_NameCardsOutsideThePack()
    {
        CardNames.Set([(46986414, "Dark Magician")]);
        try
        {
            Assert.Equal("\"Dark Magician\"", FilterText.Describe(new CardFilter { CardCode = 46986414 }, null, withLocation: false));
        }
        finally { CardNames.Set([]); }
    }

    [Fact]
    public void Database_AlwaysHasEffectStrings()
    {
        // A layout without str1..str16 (like some Omega databases): the strings still get written.
        var path = Path.Combine(Path.GetTempPath(), $"cc-str-{Guid.NewGuid():N}.db");
        try
        {
            var schema = new CdbSchema
            {
                Datas = CdbSchema.Ygopro.Datas,
                DatasSql = CdbSchema.Ygopro.DatasSql,
                Texts = [new CdbColumn("id", "integer", true, null), new CdbColumn("name", "text", false, null), new CdbColumn("desc", "text", false, null)],
                TextsSql = "CREATE TABLE texts (id integer primary key, name text, desc text)",
            };
            var row = new CdbRow { Id = 100010070, Name = "Prompted", Desc = "Text" };
            row.Strings[0] = "Special Summon this card?";
            CdbFile.Write(path, [row], schema);
            using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT str1, str16 FROM texts WHERE id=100010070";
            using var rd = cmd.ExecuteReader();
            Assert.True(rd.Read());
            Assert.Equal("Special Summon this card?", rd.GetString(0));
            Assert.Equal("", rd.GetString(1));
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }
}
