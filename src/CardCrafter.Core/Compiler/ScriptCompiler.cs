using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

public sealed class CompileResult
{
    public required string Lua { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    /// <summary>The effect labels, in order, for the database's str1..str16 columns.</summary>
    public required IReadOnlyList<string> Strings { get; init; }
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

/// <summary>Turns a card project's event sheet into a complete card script.</summary>
public static class ScriptCompiler
{
    public static CompileResult Compile(CardProject project, CardPack? pack = null)
    {
        var script = new ScriptBuilder(project, pack);
        var card = project.Card;
        var strings = project.UseCustomLua && project.ScriptStrings.Count > 0
            ? project.ScriptStrings.Take(16).ToList()
            : EffectStrings(project, pack);

        CheckCard(project, script.Diagnostics);

        if (project.UseCustomLua)
        {
            return new CompileResult { Lua = project.CustomLua, Diagnostics = script.Diagnostics, Strings = strings };
        }

        var init = new List<string>();
        init.AddRange(Procedures.Write(project, script));

        var built = new List<EffectBuilder>();
        for (int i = 0; i < project.Effects.Count; i++)
        {
            var effect = new EffectBuilder(script, project.Effects[i], i + 1);
            CompileEffect(effect, pack);
            built.Add(effect);
        }

        if (NeedsActivationEffect(card, built) && card.Kind == CardKind.Spell && card.SpellKind == SpellKind.Equip)
        {
            init.AddRange(EquipActivation(project, script));
        }
        else if (NeedsActivationEffect(card, built))
        {
            init.Add("--Activate");
            init.Add("local e0=Effect.CreateEffect(c)");
            init.Add("e0:SetType(EFFECT_TYPE_ACTIVATE)");
            init.Add("e0:SetCode(EVENT_FREE_CHAIN)");
            init.Add("c:RegisterEffect(e0)");
        }

        init.InsertRange(0, script.InitialLines);
        var limits = CountLimits(card, built);
        foreach (var effect in built)
            init.AddRange(effect.Build(limits[effect.Index]));

        var lines = new List<string>
        {
            $"--{Lua.Comment(card.Name)}",
            "--Made with YGO Card Crafter",
            $"local s,id=c{Lua.Num(card.Id)},{Lua.Num(card.Id)}",
            "function s.initial_effect(c)",
        };
        lines.AddRange(init.Select(l => "\t" + l));
        lines.Add("end");
        lines.AddRange(script.Functions);

        return new CompileResult
        {
            Lua = string.Join("\n", lines) + "\n",
            Diagnostics = script.Diagnostics,
            Strings = strings,
        };
    }

    private static void CompileEffect(EffectBuilder effect, CardPack? pack)
    {
        var src = effect.Source;
        if (src.When is null)
        {
            effect.Error("Pick a WHEN block: what starts this effect.");
            return;
        }
        if (!Run(effect, src.When, BlockKind.Trigger, pack)) return;

        foreach (var b in src.If) Run(effect, b, BlockKind.Condition, pack);

        if (effect.IsPassive && src.Cost.Count > 0)
            effect.Error("\"While face-up\" effects are never activated, so they cannot have a COST.");
        else
            foreach (var b in src.Cost) Run(effect, b, BlockKind.Cost, pack);

        if (effect.IsSummonProcedure)
        {
            if (src.Do.Count > 0)
                effect.Error("A Special Summon procedure only Summons this card, so it has no DO blocks. " +
                             "For something that happens after, add an effect with \"If this card is Special Summoned\".");
            return;
        }

        if (effect.IsMandatory && src.Cost.Count > 0)
            effect.Warn("Mandatory effects usually have no cost. If the cost can't be paid the effect still has to activate.");

        var doKind = effect.IsPassive ? BlockKind.Passive : BlockKind.Action;
        foreach (var b in src.Do) Run(effect, b, doKind, pack);

        if (src.Do.Count == 0)
            effect.Error("Add at least one DO block: what the effect does.");
        if (effect.IsPassive && src.Limit != OncePerTurn.None)
            effect.Warn("Once-per-turn limits do nothing on \"while face-up\" effects and were ignored.");
    }

    private static bool Run(EffectBuilder effect, BlockInstance inst, BlockKind expected, CardPack? pack)
    {
        var def = BlockRegistry.Find(inst.BlockId);
        if (def is null)
        {
            effect.Error($"Unknown block \"{inst.BlockId}\". It may come from a newer version of Card Crafter.");
            return false;
        }
        if (def.Kind != expected)
        {
            var where = expected switch
            {
                BlockKind.Passive => "This effect is a \"while face-up\" effect, so its DO blocks must be lasting effects",
                BlockKind.Action when def.Kind == BlockKind.Passive => "Lasting effects only work with the \"While this card is face-up\" trigger",
                _ => $"This block belongs in {Column(def.Kind)}, not {Column(expected)}",
            };
            effect.Error($"{where} (\"{def.Name}\").");
            return false;
        }
        def.Compile(new BlockContext(new BlockArgs(def, inst, pack), effect));
        return true;
    }

    private static string Column(BlockKind k) => k switch
    {
        BlockKind.Trigger => "WHEN",
        BlockKind.Condition => "IF",
        BlockKind.Cost => "COST",
        _ => "DO",
    };

    /// <summary>
    /// An Equip Spell's activation: target a face-up monster and equip this card to it, plus the equip limit that
    /// keeps it on monsters <see cref="CardProject.EquipFilter"/> allows.
    /// </summary>
    private static IEnumerable<string> EquipActivation(CardProject project, ScriptBuilder script)
    {
        var f = project.EquipFilter;
        var self = f.Owner == CardOwner.Opponent ? "0" : "LOCATION_MZONE";
        var opp = f.Owner == CardOwner.You ? "0" : "LOCATION_MZONE";
        var conds = FilterCompiler.Conditions(new CardFilter
        {
            CardType = FilterCardType.Monster, MonsterKind = f.MonsterKind, Race = f.Race, Attribute = f.Attribute, Archetype = f.Archetype,
            CardCode = f.CardCode, LevelMin = f.LevelMin, LevelMax = f.LevelMax, AtkMin = f.AtkMin, AtkMax = f.AtkMax,
        });
        conds.Remove("c:IsType(TYPE_MONSTER)");
        var test = conds.Count == 0 ? "true" : string.Join(" and ", conds);
        var limit = conds.Count == 0 ? "1" : script.AddFunction("eqlimit", "e,c", [$"return {test}"]);
        var filter = script.AddFunction("eqfilter", "c", [$"return c:IsFaceup() and {test}"]);
        var tg = script.AddFunction("eqtg", "e,tp,eg,ep,ev,re,r,rp,chk,chkc",
        [
            $"if chkc then return chkc:IsLocation(LOCATION_MZONE) and {filter}(chkc) end",
            $"if chk==0 then return Duel.IsExistingTarget({filter},tp,{self},{opp},1,nil) end",
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_EQUIP)",
            $"Duel.SelectTarget(tp,{filter},tp,{self},{opp},1,1,nil)",
            "Duel.SetOperationInfo(0,CATEGORY_EQUIP,e:GetHandler(),1,0,0)",
        ]);
        var op = script.AddFunction("eqop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local c=e:GetHandler()",
            "local tc=Duel.GetFirstTarget()",
            "if c:IsRelateToEffect(e) and tc:IsRelateToEffect(e) and tc:IsFaceup() then",
            "	Duel.Equip(tp,c,tc)",
            "end",
        ]);
        return
        [
            "--Activate: equip to a monster",
            "local e0=Effect.CreateEffect(c)",
            "e0:SetCategory(CATEGORY_EQUIP)",
            "e0:SetType(EFFECT_TYPE_ACTIVATE)",
            "e0:SetCode(EVENT_FREE_CHAIN)",
            "e0:SetProperty(EFFECT_FLAG_CARD_TARGET+EFFECT_FLAG_CONTINUOUS_TARGET)",
            $"e0:SetTarget({tg})",
            $"e0:SetOperation({op})",
            "c:RegisterEffect(e0)",
            "local el=Effect.CreateEffect(c)",
            "el:SetType(EFFECT_TYPE_SINGLE)",
            "el:SetCode(EFFECT_EQUIP_LIMIT)",
            "el:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            $"el:SetValue({limit})",
            "c:RegisterEffect(el)",
        ];
    }

    /// <summary>
    /// The database's str1..str16: what the game shows when it asks whether to activate an effect. Each is the
    /// effect's own card text, so the prompt says the whole effect; an effect with no text gets its name.
    /// </summary>
    private static List<string> EffectStrings(CardProject project, CardPack? pack) =>
        project.Effects.Take(16).Select(e => Text.EffectTextGenerator.Describe(e, project.Card, pack) is { Length: > 0 } t ? t : e.Label).ToList();

    /// <summary>Continuous, Field and Equip cards need a plain "activate this card" effect to be placed on the field.</summary>
    private static bool NeedsActivationEffect(CardData card, List<EffectBuilder> effects)
    {
        bool lingering = card.Kind switch
        {
            CardKind.Spell => card.SpellKind is SpellKind.Continuous or SpellKind.Field or SpellKind.Equip,
            CardKind.Trap => card.TrapKind == TrapKind.Continuous,
            _ => false,
        };
        return lingering && !effects.Any(e => e.IsActivation);
    }

    /// <summary>Works out SetCountLimit arguments, giving each hard once-per-turn group its own code.</summary>
    private static Dictionary<int, string?> CountLimits(CardData card, List<EffectBuilder> effects)
    {
        var result = new Dictionary<int, string?>();
        var groups = new List<int>();
        foreach (var e in effects)
        {
            string? limit = null;
            if (!e.IsPassive)
            {
                switch (e.Source.Limit)
                {
                    case OncePerTurn.Soft:
                        limit = "1";
                        break;
                    case OncePerTurn.Hard:
                        var group = e.Source.SharedLimitGroup > 0 ? e.Source.SharedLimitGroup : -e.Index;
                        if (!groups.Contains(group)) groups.Add(group);
                        var slot = groups.IndexOf(group);
                        var code = slot == 0 ? "id" : $"id+{Lua.Num(slot * card.CountCodeOffset)}";
                        // For Spell/Trap activations, "you can only activate 1 per turn" ignores negated activations,
                        // and a Summon procedure only counts once it is used.
                        limit = e.IsActivation || e.IsSummonProcedure ? $"1,{code}+EFFECT_COUNT_CODE_OATH" : $"1,{code}";
                        break;
                }
            }
            result[e.Index] = limit;
        }
        return result;
    }

    private static void CheckCard(CardProject project, List<Diagnostic> d)
    {
        var card = project.Card;
        if (card.Id <= 0 || card.Id > uint.MaxValue)
            d.Add(new(Severity.Error, "The card's passcode must be a positive number up to 4294967295."));
        else if (card.Id < 100000000)
            d.Add(new(Severity.Warning, "Passcodes under 9 digits can clash with official cards. Custom cards usually use 9-digit codes."));
        if (string.IsNullOrWhiteSpace(card.Name))
            d.Add(new(Severity.Error, "The card needs a name."));
        if (project.Effects.Count > 16)
            d.Add(new(Severity.Error, "A card can have at most 16 effects (the database has 16 text slots)."));
        if (card.Archetypes.Count > 4)
            d.Add(new(Severity.Error, "A card can belong to at most 4 archetypes."));
        if (card.IsMonster && card.Frame is MonsterFrame.Normal or MonsterFrame.Token && project.Effects.Count > 0)
            d.Add(new(Severity.Warning, "Normal monsters have no effects. Change the frame to Effect so the game runs them."));
        if (card.IsLink && card.LinkMarkers == LinkMarker.None)
            d.Add(new(Severity.Error, "Link monsters need at least one Link Arrow."));
        if (card.IsMonster && card.SummonRule != SummonRule.Normal && card.IsMainDeckNonRitual
            && !project.Effects.Any(e => e.When?.BlockId == SummonProcedureTrigger.BlockId))
        {
            d.Add(new(Severity.Warning, card.SummonRule == SummonRule.ProcedureOnly
                ? "This monster can only be Special Summoned by its own procedure, but it has none, so it can never be Summoned. Add an effect with the WHEN block \"Special Summon procedure\"."
                : "This monster can't be Normal Summoned and has no Special Summon procedure, so only other cards can Special Summon it. Add an effect with the WHEN block \"Special Summon procedure\" if it should Summon itself."));
        }
        if (card.IsExtraDeck && project.Procedure.Kind == SummonProcedureKind.None)
            d.Add(new(Severity.Warning, $"This {card.Frame} monster has no summon procedure, so it can't be summoned. Set one on the Card tab."));
    }
}
