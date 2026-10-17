using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CardCrafter.Core.Assistant;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Templates;

namespace CardCrafter.Core.Tests;

/// <summary>The assistant's guide and tools, and a conversation with a stand-in for the API.</summary>
public class AssistantTests
{
    private static (PackWorkspace Workspace, AssistantTools Tools) Monster()
    {
        var pack = new CardPack();
        pack.Cards.Add(new CardProject { Card = new CardData { Id = 100099500, Name = "Helper Test", Kind = CardKind.Monster } });
        var ws = new PackWorkspace(pack);
        return (ws, new AssistantTools(ws));
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TheGuide_ListsEveryBlock()
    {
        foreach (var kind in Enum.GetValues<BlockKind>())
            foreach (var b in BlockRegistry.Offered(kind))
                Assert.Contains($"`{b.Id}`", AssistantGuide.SystemPrompt);
    }

    [Fact]
    public void EveryToolSchema_IsValidAndNamesItsRequiredFields()
    {
        Assert.Equal(AssistantTools.All.Count, AssistantTools.All.Select(t => t.Name).Distinct().Count());
        foreach (var t in AssistantTools.All)
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(t.InputSchema)!;
            Assert.All(t.Required, r => Assert.True(props.ContainsKey(r), $"{t.Name}: {r}"));
            Assert.All(props.Values, p => Assert.Equal(JsonValueKind.Object, p.ValueKind));
        }
    }

    [Fact]
    public void DescribeBlock_GivesItsOptions()
    {
        var (_, tools) = Monster();
        var outcome = tools.Run("describe_block", J("""{"block_id":"do.draw"}"""));
        Assert.False(outcome.IsError, outcome.Content);
        Assert.Contains("\"key\": \"count\"", outcome.Content);
        Assert.True(tools.Run("describe_block", J("""{"block_id":"do.nothing_like_this"}""")).IsError);
        Assert.Contains("do.draw", tools.Run("find_blocks", J("""{"query":"draw","kind":"do"}""")).Content);
    }

    [Fact]
    public void DescribeModel_ListsFieldsAndRequirementProperties()
    {
        var (_, tools) = Monster();
        Assert.Contains("\"name\": \"atk\"", tools.Run("describe_model", J("""{"type":"card"}""")).Content);
        Assert.Contains("\"property\": \"base_atk\"", tools.Run("describe_model", J("""{"type":"requirement"}""")).Content);
        Assert.Contains("\"name\": \"faceUpOnly\"", tools.Run("describe_model", J("""{"type":"filter"}""")).Content);
    }

    [Fact]
    public void AddEffect_BuildsTheBlocksAndAnswersWithTheCardText()
    {
        var (ws, tools) = Monster();
        var outcome = tools.Run("add_effect", J("""
            {"effect":{"label":"Draw","limit":"hard","when":{"blockId":"when.summoned"},"do":[{"blockId":"do.draw","values":{"count":2}}]}}
            """));
        Assert.False(outcome.IsError, outcome.Content);
        Assert.NotNull(outcome.Activity);
        Assert.Equal(1, ws.Changes);
        var effect = Assert.Single(ws.Card!.Effects);
        Assert.Equal(OncePerTurn.Hard, effect.Limit);
        Assert.Equal("2", effect.Do[0].Values["count"]);
        Assert.Contains("draw 2 cards", outcome.Content);
    }

    [Fact]
    public void WrongOptions_AreRefusedWithWhatIsAllowed()
    {
        var (ws, tools) = Monster();
        var bad = tools.Run("add_effect", J("""{"effect":{"when":{"blockId":"when.summoned"},"do":[{"blockId":"do.draw","values":{"cards":2}}]}}"""));
        Assert.True(bad.IsError);
        Assert.Contains("count", bad.Content);
        var range = tools.Run("add_effect", J("""{"effect":{"when":{"blockId":"when.summoned"},"do":[{"blockId":"do.draw","values":{"count":-3}}]}}"""));
        Assert.True(range.IsError);
        Assert.Contains("must be from", range.Content);
        var slot = tools.Run("add_effect", J("""{"effect":{"when":{"blockId":"do.draw"}}}"""));
        Assert.True(slot.IsError);
        Assert.Contains("can't go in when", slot.Content);
        Assert.Empty(ws.Card!.Effects);
        Assert.Equal(0, ws.Changes);
    }

    [Fact]
    public void UpdateCard_ChangesOnlyTheGivenFields()
    {
        var (ws, tools) = Monster();
        var outcome = tools.Run("update_card", J("""{"card":{"name":"Ember Knight","attribute":"fire","atk":2100}}"""));
        Assert.False(outcome.IsError, outcome.Content);
        Assert.Equal(("Ember Knight", CardAttribute.Fire, 2100, 100099500L), (ws.Card!.Card.Name, ws.Card.Card.Attribute, ws.Card.Card.Atk, ws.Card.Card.Id));
        Assert.True(tools.Run("update_card", J("""{"card":{"power":9000}}""")).IsError);
    }

    /// <summary>The JSON the tools give is the JSON they take: every template survives the trip and compiles the same.</summary>
    [Fact]
    public void EveryTemplate_RoundTripsThroughTheEffectJson()
    {
        foreach (var t in EffectTemplates.All)
        {
            var original = t.Create();
            var (read, errors) = AssistantTools.ReadEffect(J(AssistantTools.EffectJson(original)));
            Assert.True(read is not null, t.Name + ": " + string.Join("\n", errors));
            var card = new CardData { Id = 100099501, Name = "Round Trip", Kind = t.For };
            Assert.Equal(ScriptCompiler.Compile(new CardProject { Card = card, Effects = [original] }).Lua,
                ScriptCompiler.Compile(new CardProject { Card = card, Effects = [read!] }).Lua);
        }
    }

    [Fact]
    public void GetCard_EffectsCanBeSentBackUnchanged()
    {
        var (ws, tools) = Monster();
        ws.Card!.Effects.Add(EffectTemplates.All.First(t => t.Name == "Search on summon").Create());
        var card = JsonNode.Parse(tools.Run("get_card", J("{}")).Content)!;
        var effect = card["effects"]![0]!.AsObject();
        Assert.Equal(1, (int)effect["number"]!);
        var outcome = tools.Run("replace_effect", J($$"""{"number":1,"effect":{{effect.ToJsonString()}}}"""));
        Assert.False(outcome.IsError, outcome.Content);
    }

    [Fact]
    public void NewAndSelectCard_WorkOnThePack()
    {
        var (ws, tools) = Monster();
        Assert.False(tools.Run("new_card", J("""{"kind":"trap"}""")).IsError);
        Assert.Equal(CardKind.Trap, ws.Card!.Card.Kind);
        Assert.False(tools.Run("select_card", J("""{"index":0}""")).IsError);
        Assert.Equal("Helper Test", ws.Card!.Card.Name);
        Assert.True(tools.Run("select_card", J("""{"index":7}""")).IsError);
        Assert.Contains("\"index\": 1", tools.Run("list_cards", J("{}")).Content);
    }

    [Fact]
    public void TranslateScript_ReportsEachPart()
    {
        var (_, tools) = Monster();
        var outcome = tools.Run("translate_script", J(JsonSerializer.Serialize(new
        {
            lua = """
                function c100099502.initial_effect(c)
                	local e1=Effect.CreateEffect(c)
                	e1:SetCategory(CATEGORY_DRAW)
                	e1:SetType(EFFECT_TYPE_IGNITION)
                	e1:SetRange(LOCATION_MZONE)
                	e1:SetProperty(EFFECT_FLAG_PLAYER_TARGET)
                	e1:SetTarget(c100099502.tg)
                	e1:SetOperation(c100099502.op)
                	c:RegisterEffect(e1)
                end
                function c100099502.tg(e,tp,eg,ep,ev,re,r,rp,chk)
                	if chk==0 then return Duel.IsPlayerCanDraw(tp,1) end
                	Duel.SetTargetPlayer(tp)
                	Duel.SetTargetParam(1)
                	Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,1)
                end
                function c100099502.op(e,tp,eg,ep,ev,re,r,rp)
                	local p,d=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER,CHAININFO_TARGET_PARAM)
                	Duel.Draw(p,d,REASON_EFFECT)
                end
                """,
        })));
        Assert.False(outcome.IsError, outcome.Content);
        Assert.Contains("\"parts\"", outcome.Content);
    }

    // ---------------------------------------------------------------- the conversation

    /// <summary>Answers each request with the next canned reply and keeps the requests.</summary>
    private sealed class FakeApi(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body) = replies[Math.Min(_next++, replies.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string Message(string stop, string content) => $$$"""
        {"id":"msg_{{{Guid.NewGuid():N}}}","type":"message","role":"assistant","model":"claude-opus-5-5","content":{{{content}}},
         "stop_reason":"{{{stop}}}","stop_sequence":null,"usage":{"input_tokens":120,"output_tokens":30,"cache_read_input_tokens":100,"cache_creation_input_tokens":0}}
        """;

    [Fact]
    public async Task ATurn_RunsTheToolsClaudeAsksForAndShowsTheReply()
    {
        var (ws, _) = Monster();
        var api = new FakeApi(
            (HttpStatusCode.OK, Message("tool_use", """
                [{"type":"text","text":"I'll add the draw effect."},
                 {"type":"tool_use","id":"toolu_1","name":"add_effect","input":{"effect":{"when":{"blockId":"when.summoned"},"do":[{"blockId":"do.draw","values":{"count":1}}]}}}]
                """)),
            (HttpStatusCode.OK, Message("end_turn", """[{"type":"text","text":"Done: it draws 1 card when Summoned."}]""")));
        var session = new AssistantSession("sk-ant-test", ws, new HttpClient(api));
        var updates = new List<AssistantUpdate>();
        await session.Send("Make it draw 1 card when it is Summoned.", updates.Add);

        Assert.Contains(updates, u => u.Kind == AssistantUpdateKind.Activity);
        Assert.Equal("Done: it draws 1 card when Summoned.", updates.Last().Text);
        Assert.Single(ws.Card!.Effects);
        Assert.Equal(2, api.Requests.Count);
        // Input tokens count the cached part too: 120 sent + 100 read from the cache, twice.
        Assert.Equal(2 * 220, session.InputTokens);

        // The request: the model, the guide, every tool, caching, effort and the server-side fallback.
        var first = JsonNode.Parse(api.Requests[0].Body)!;
        Assert.Equal("claude-opus-5-5", (string)first["model"]!);
        Assert.Equal(AssistantTools.All.Count, first["tools"]!.AsArray().Count);
        Assert.NotNull(first["cache_control"]);
        Assert.Equal("medium", (string)first["output_config"]!["effort"]!);
        Assert.Equal("default", (string)first["fallbacks"]!);
        Assert.Contains("server-side-fallback-2026-07-01", string.Join(",", api.Requests[0].Request.Headers.GetValues("anthropic-beta")));
        Assert.Equal("sk-ant-test", api.Requests[0].Request.Headers.GetValues("x-api-key").Single());

        // The second request sends Claude's tool call back with its answer, after the user's message.
        var messages = JsonNode.Parse(api.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Contains("Helper Test", messages[0]!["content"]!.ToJsonString());
        Assert.Equal("tool_use", (string)messages[1]!["content"]![1]!["type"]!);
        var result = messages[2]!["content"]![0]!;
        Assert.Equal(("tool_result", "toolu_1"), ((string)result["type"]!, (string)result["tool_use_id"]!));
    }

    [Fact]
    public async Task ARefusedKey_IsExplainedAndTheConversationStaysAsItWas()
    {
        var (ws, _) = Monster();
        var api = new FakeApi((HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
        var session = new AssistantSession("sk-ant-wrong", ws, new HttpClient(api));
        var updates = new List<AssistantUpdate>();
        await session.Send("Hello", updates.Add);
        var problem = Assert.Single(updates);
        Assert.Equal(AssistantUpdateKind.Problem, problem.Kind);
        Assert.Contains("API key", problem.Text);
        Assert.True(session.IsEmpty);
    }

    [Fact]
    public async Task ADeclinedRequest_LeavesNothingInTheHistory()
    {
        var (ws, _) = Monster();
        var api = new FakeApi((HttpStatusCode.OK, Message("refusal", "[]")));
        var session = new AssistantSession("sk-ant-test", ws, new HttpClient(api));
        var updates = new List<AssistantUpdate>();
        await session.Send("Hello", updates.Add);
        Assert.Equal(AssistantUpdateKind.Problem, Assert.Single(updates).Kind);
        Assert.True(session.IsEmpty);
    }

    [Fact]
    public async Task ModelsWithoutServerFallback_DontAskForIt()
    {
        var (ws, _) = Monster();
        var api = new FakeApi((HttpStatusCode.OK, Message("end_turn", """[{"type":"text","text":"Hi"}]""")));
        var session = new AssistantSession("sk-ant-test", ws, new HttpClient(api)) { Model = AssistantModel.Find("claude-haiku-5-5"), Effort = "low" };
        await session.Send("Hello", _ => { });
        var body = JsonNode.Parse(api.Requests[0].Body)!;
        Assert.Null(body["fallbacks"]);
        Assert.Equal("low", (string)body["output_config"]!["effort"]!);
    }

    // ---------------------------------------------------------------- OpenAI-compatible services

    private static string Completion(string message, string finish = "stop") => $$$$"""
        {"id":"chatcmpl-1","object":"chat.completion","model":"test-model","choices":[{"index":0,"message":{{{{message}}}},"finish_reason":"{{{{finish}}}}"}],
         "usage":{"prompt_tokens":200,"completion_tokens":40,"prompt_tokens_details":{"cached_tokens":150}}}
        """;

    [Fact]
    public async Task AnOpenAiCompatibleService_GetsTheSameGuideAndTools()
    {
        var (ws, _) = Monster();
        var api = new FakeApi(
            (HttpStatusCode.OK, Completion("""
                {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"add_effect",
                 "arguments":"{\"effect\":{\"when\":{\"blockId\":\"when.summoned\"},\"do\":[{\"blockId\":\"do.draw\",\"values\":{\"count\":1}}]}}"}},
                 {"id":"call_2","type":"function","function":{"name":"get_card","arguments":"not json"}}]}
                """, "tool_calls")),
            (HttpStatusCode.OK, Completion("""{"role":"assistant","content":"Added: it draws 1 card when Summoned."}""")));
        var provider = AssistantProvider.Find("openai");
        var chat = new OpenAiCompatibleSession(provider, "https://example.test/v1/", "sk-test", ws, new HttpClient(api)) { Model = "test-model" };
        var updates = new List<AssistantUpdate>();
        await chat.Send("Make it draw 1 card when it is Summoned.", updates.Add);

        Assert.Single(ws.Card!.Effects);
        Assert.Contains(updates, u => u.Kind == AssistantUpdateKind.Activity);
        Assert.Equal("Added: it draws 1 card when Summoned.", updates.Last().Text);
        Assert.Equal((400, 80, 300), (chat.InputTokens, chat.OutputTokens, chat.CachedTokens));

        var first = api.Requests[0];
        Assert.Equal("https://example.test/v1/chat/completions", first.Request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", first.Request.Headers.Authorization!.ToString());
        var body = JsonNode.Parse(first.Body)!;
        Assert.Equal("test-model", (string)body["model"]!);
        Assert.Equal(("system", AssistantGuide.SystemPrompt), ((string)body["messages"]![0]!["role"]!, (string)body["messages"]![0]!["content"]!));
        var tools = body["tools"]!.AsArray();
        Assert.Equal(AssistantTools.All.Count, tools.Count);
        Assert.Equal("object", (string)tools[0]!["function"]!["parameters"]!["type"]!);

        // Both calls are answered as tool messages; the one with broken arguments as an error the model can fix.
        var messages = JsonNode.Parse(api.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal(5, messages.Count);
        Assert.Equal(("tool", "call_1"), ((string)messages[3]!["role"]!, (string)messages[3]!["tool_call_id"]!));
        Assert.StartsWith("Error:", (string)messages[4]!["content"]!);
    }

    [Fact]
    public async Task AServiceError_IsExplainedAndForgotten()
    {
        var (ws, _) = Monster();
        var api = new FakeApi((HttpStatusCode.BadRequest, """{"error":{"message":"tools are not supported by this model"}}"""));
        var chat = new OpenAiCompatibleSession(AssistantProvider.Find("ollama"), "http://localhost:11434/v1", "", ws, new HttpClient(api)) { Model = "tiny" };
        var updates = new List<AssistantUpdate>();
        await chat.Send("Hello", updates.Add);
        var problem = Assert.Single(updates);
        Assert.Contains("tools are not supported", problem.Text);
        Assert.True(chat.IsEmpty);
        Assert.Null(api.Requests[0].Request.Headers.Authorization);
    }

    [Fact]
    public async Task Models_AreListedWithoutGeminisPrefix()
    {
        var api = new FakeApi((HttpStatusCode.OK, """{"object":"list","data":[{"id":"models/gemini-test"},{"id":"another-model"}]}"""));
        var models = await OpenAiCompatibleSession.ListModels("https://example.test/v1", "k", new HttpClient(api));
        Assert.Equal(["another-model", "gemini-test"], models);
        Assert.EndsWith("/v1/models", api.Requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public void EveryProvider_HasAnAddressOrLetsOneBeTyped()
    {
        Assert.Equal(AssistantProvider.All.Count, AssistantProvider.All.Select(p => p.Id).Distinct().Count());
        Assert.All(AssistantProvider.All, p => Assert.True(p.EditableUrl || Uri.IsWellFormedUriString(p.BaseUrl, UriKind.Absolute), p.Id));
        Assert.True(AssistantProvider.Default.IsClaude);
    }

    [Fact]
    public async Task AnErrorSentAsAList_IsExplained_NotACrash()
    {
        // Gemini's OpenAI-compatible endpoint answers some errors as a one-item JSON list.
        var (ws, _) = Monster();
        var api = new FakeApi((HttpStatusCode.BadRequest, """[{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}]"""));
        var chat = new OpenAiCompatibleSession(AssistantProvider.Find("gemini"), "https://example.test/v1", "bad", ws, new HttpClient(api)) { Model = "gemini-test" };
        var updates = new List<AssistantUpdate>();
        await chat.Send("Hello", updates.Add);
        Assert.Contains("API key not valid", Assert.Single(updates).Text);
        await Assert.ThrowsAsync<ProviderException>(() => OpenAiCompatibleSession.ListModels("https://example.test/v1", "bad", new HttpClient(api)));
    }

    [Fact]
    public async Task OddlyShapedReplies_AreReadOrReported()
    {
        // Arguments sent as an object instead of a JSON string, a number where text was expected, and a reply with no choices.
        var (ws, _) = Monster();
        var api = new FakeApi(
            (HttpStatusCode.OK, Completion("""{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"list_cards","arguments":{}}}]}""", "tool_calls")),
            (HttpStatusCode.OK, """{"id":"x","choices":{"weird":true},"usage":{"prompt_tokens":"many"}}"""));
        var chat = new OpenAiCompatibleSession(AssistantProvider.Find("openai"), "https://example.test/v1", "k", ws, new HttpClient(api)) { Model = "m" };
        var updates = new List<AssistantUpdate>();
        await chat.Send("Hello", updates.Add);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(AssistantUpdateKind.Problem, updates.Last().Kind);
        Assert.True(chat.IsEmpty);
    }
}
