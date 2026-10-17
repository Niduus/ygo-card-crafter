using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CardCrafter.Core.Assistant;

/// <summary>
/// A conversation through the OpenAI Chat Completions format with function calling, which OpenAI, Google Gemini,
/// OpenRouter, xAI, Mistral, DeepSeek, Groq, Ollama, LM Studio and many others accept. It gets the same guide and the same
/// tools as Claude; the tools' answers go back as "tool" messages until the model replies.
/// </summary>
public sealed class OpenAiCompatibleSession : IAssistantChat
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _key;
    private readonly AssistantProvider _provider;
    private readonly AssistantTools _tools;
    private readonly IAssistantWorkspace _workspace;
    private readonly List<JsonNode> _messages = [];
    private static readonly JsonArray Tools = BuildTools();

    /// <param name="baseUrl">The API root, such as https://api.openai.com/v1.</param>
    /// <param name="http">For tests: the HTTP client the requests go through.</param>
    public OpenAiCompatibleSession(AssistantProvider provider, string baseUrl, string apiKey, IAssistantWorkspace workspace, HttpClient? http = null)
    {
        _provider = provider;
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _key = apiKey.Trim();
        _workspace = workspace;
        _tools = new AssistantTools(workspace);
        // Local models can take minutes on a long answer.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>The model's id at the provider, such as "gpt-…" or "gemini-…".</summary>
    public string Model { get; set; } = "";

    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CachedTokens { get; private set; }
    public bool IsEmpty => _messages.Count == 0;

    public void Clear()
    {
        _messages.Clear();
        InputTokens = OutputTokens = CachedTokens = 0;
    }

    public async Task Send(string text, Action<AssistantUpdate> update, CancellationToken cancel = default)
    {
        if (Model.Trim().Length == 0)
        {
            update(new(AssistantUpdateKind.Problem, "Pick or type a model first (\"Load models\" lists what this service has)."));
            return;
        }
        if (_baseUrl.Length == 0)
        {
            update(new(AssistantUpdateKind.Problem, "Enter the service's address first."));
            return;
        }
        var start = _messages.Count;
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = AssistantGuide.Context(_workspace) + text });
        try
        {
            for (var round = 0; round < AssistantSession.MaxRounds; round++)
            {
                var reply = await Complete(cancel);
                var choice = Item(Field(reply, "choices"), 0);
                if (Field(choice, "message") is not JsonObject message)
                    throw new ProviderException(Detail(reply) is { Length: > 0 } why ? why : "The service answered without a message.");
                Count(Field(reply, "usage"));

                // The reply goes back exactly as it came, with its tool calls.
                var echo = new JsonObject { ["role"] = "assistant", ["content"] = message["content"]?.DeepClone() };
                var calls = message["tool_calls"] as JsonArray;
                if (calls is { Count: > 0 }) echo["tool_calls"] = calls.DeepClone();
                _messages.Add(echo);

                if (Text(message["content"]) is { Length: > 0 } said) update(new(AssistantUpdateKind.Reply, said));
                if (calls is not { Count: > 0 })
                {
                    if (Str(Field(choice, "finish_reason")) == "length") update(new(AssistantUpdateKind.Problem, "The reply was cut off because it got too long. Ask it to continue."));
                    else if (Str(Field(choice, "finish_reason")) == "content_filter") update(new(AssistantUpdateKind.Problem, "The service's content filter stopped the reply."));
                    return;
                }

                foreach (var call in calls)
                {
                    cancel.ThrowIfCancellationRequested();
                    var id = Str(Field(call, "id")) ?? "";
                    var name = Str(Field(Field(call, "function"), "name")) ?? "";
                    var outcome = Arguments(Field(Field(call, "function"), "arguments")) is { } input
                        ? _tools.Run(name, input)
                        : new ToolOutcome("The arguments weren't valid JSON. Send the call again with a JSON object.", true);
                    if (outcome.Activity is { } did) update(new(AssistantUpdateKind.Activity, did));
                    _messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["content"] = outcome.IsError ? "Error: " + outcome.Content : outcome.Content,
                    });
                }
            }
            update(new(AssistantUpdateKind.Problem, $"The model used {AssistantSession.MaxRounds} rounds of tools without finishing. Send another message to let it go on."));
            _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "I stopped after many steps; tell me to go on if you want me to continue." });
        }
        // Anything else (a reply in a shape no service should send) is reported too, never left to crash the app.
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or ProviderException or JsonException or InvalidOperationException or FormatException)
        {
            _messages.RemoveRange(start, _messages.Count - start);
            update(new(AssistantUpdateKind.Problem, Explain(ex)));
        }
    }

    /// <summary>The models the service offers (GET /models), for the model picker.</summary>
    public static async Task<IReadOnlyList<string>> ListModels(string baseUrl, string apiKey, HttpClient? http = null, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.Trim().TrimEnd('/') + "/models");
        if (apiKey.Trim().Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await (http ?? Shared).SendAsync(request, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, body);
        var models = Field(Unwrap(JsonNode.Parse(body)), "data") as JsonArray ?? [];
        // Gemini lists its models as "models/gemini-…"; requests take the name without the prefix.
        return models.Select(m => Str(Field(m, "id"))).OfType<string>().Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };

    private async Task<JsonNode> Complete(CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = Model.Trim(),
            ["messages"] = new JsonArray([new JsonObject { ["role"] = "system", ["content"] = AssistantGuide.SystemPrompt }, .. _messages.Select(m => m.DeepClone())]),
            ["tools"] = Tools.DeepClone(),
            ["tool_choice"] = "auto",
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        // OpenRouter shows which app a request came from.
        if (_provider.Id == "openrouter") request.Headers.Add("X-Title", "YGO Card Crafter");
        using var response = await _http.SendAsync(request, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, text);
        return Unwrap(JsonNode.Parse(text)) ?? throw new ProviderException("The service sent an empty answer.");
    }

    private void Count(JsonNode? usage)
    {
        if (usage is null) return;
        InputTokens += Num(Field(usage, "prompt_tokens"));
        OutputTokens += Num(Field(usage, "completion_tokens"));
        CachedTokens += Num(Field(Field(usage, "prompt_tokens_details"), "cached_tokens"));
    }

    /// <summary>The message's text: a string, or a list of text parts.</summary>
    private static string? Text(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Trim(),
        JsonArray parts => string.Join("\n", parts.Select(p => Str(Field(p, "text"))).OfType<string>()).Trim(),
        _ => null,
    };

    /// <summary>A call's arguments: a JSON string as the format says, or (as some services send) the object itself.</summary>
    private static JsonElement? Arguments(JsonNode? arguments)
    {
        if (arguments is JsonObject direct) return JsonSerializer.SerializeToElement(direct);
        var json = Str(arguments);
        try
        {
            var el = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement;
            return el.ValueKind == JsonValueKind.Object ? el : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray BuildTools() => new(AssistantTools.All.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = JsonNode.Parse(t.InputSchema),
                ["required"] = new JsonArray(t.Required.Select(r => (JsonNode)r).ToArray()),
            },
        },
    }).ToArray());

    // ---- reading replies safely: services differ in what they send, so a missing or differently shaped field is
    // treated as absent instead of throwing.

    private static JsonNode? Field(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;
    private static JsonNode? Item(JsonNode? node, int index) => node is JsonArray a && index < a.Count ? a[index] : null;
    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long Num(JsonNode? node) =>
        node is JsonValue v ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : 0 : 0;

    /// <summary>Gemini's OpenAI-compatible endpoint sends some answers, errors in particular, as a one-item list.</summary>
    private static JsonNode? Unwrap(JsonNode? node) => node is JsonArray { Count: > 0 } a ? a[0] : node;

    /// <summary>An error's message, from {"error":{"message":…}}, {"error":"…"} or {"message":…}.</summary>
    private static string Detail(JsonNode? reply) =>
        Str(Field(Field(reply, "error"), "message")) ?? Str(Field(reply, "error")) ?? Str(Field(reply, "message")) ?? "";

    private static ProviderException Failure(HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            detail = Detail(Unwrap(JsonNode.Parse(body)));
        }
        catch (JsonException)
        {
            detail = body.Length > 300 ? body[..300] : body;
        }
        return new ProviderException(detail, status);
    }

    /// <summary>What went wrong, in words the user can act on.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        OperationCanceledException => "Stopped.",
        ProviderException { Status: HttpStatusCode.Unauthorized } => "The API key was not accepted. Check that it is copied whole and still active." + Detail(ex),
        ProviderException { Status: HttpStatusCode.Forbidden } => "This key isn't allowed to do that (or the account needs credit)." + Detail(ex),
        ProviderException { Status: HttpStatusCode.NotFound } => "The service or model wasn't found. Check the model name and the address." + Detail(ex),
        ProviderException { Status: HttpStatusCode.TooManyRequests } => "Too many requests for now (rate limit), or the account is out of credit. Wait a moment, or check billing." + Detail(ex),
        ProviderException { Status: HttpStatusCode.BadRequest } => "The service refused the request. The model may not support tools (function calling); pick one that does." + Detail(ex),
        ProviderException { Status: >= HttpStatusCode.InternalServerError } => "The service had a problem. Try again in a moment." + Detail(ex),
        ProviderException p => p.Message,
        HttpRequestException => "Couldn't reach the service. Check the address and the internet connection; a program on this computer (Ollama, LM Studio) must be running.",
        JsonException => "The service sent an answer that isn't JSON. Check the address: it should be the API root, often ending in /v1.",
        _ => $"The service sent an answer Card Crafter didn't expect: {ex.Message}",
    };

    private static string Detail(Exception ex) => ex.Message is { Length: > 0 } m ? $" ({m})" : "";
}

/// <summary>An error the service reported.</summary>
public sealed class ProviderException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}
