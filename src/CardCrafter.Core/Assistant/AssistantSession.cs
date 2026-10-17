using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace CardCrafter.Core.Assistant;

/// <summary>A Claude model the assistant can use.</summary>
/// <param name="ServerFallback">Whether the API can re-run a declined request on another model by itself.</param>
public sealed record AssistantModel(string Id, string Label, bool ServerFallback = true)
{
    public static IReadOnlyList<AssistantModel> All { get; } =
    [
        new("claude-opus-5-5", "Claude Opus 5.5 (recommended)"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5 (faster, cheaper)"),
        new("claude-haiku-5-5", "Claude Haiku 5.5 (fastest, cheapest)", ServerFallback: false),
        new("claude-fable-5-1", "Claude Fable 5.1 (most capable, most expensive)"),
    ];

    public static AssistantModel Default => All[0];

    public static AssistantModel Find(string? id) => All.FirstOrDefault(m => m.Id == id) ?? Default;

    public override string ToString() => Label;
}

/// <summary>Something that happened during a turn, for the chat: the reply, a change the assistant made, or a problem.</summary>
public sealed record AssistantUpdate(AssistantUpdateKind Kind, string Text);

public enum AssistantUpdateKind { Reply, Activity, Problem }

/// <summary>
/// One conversation with Claude about the user's cards. Each turn sends the conversation so far with Card Crafter's guide and
/// tools; whenever Claude calls a tool, the tool runs on the open pack and its answer goes back, until Claude replies.
/// The history only ever grows (nothing earlier is edited), which keeps the cached prompt and Claude's reasoning valid.
/// </summary>
public sealed class AssistantSession : IAssistantChat
{
    /// <summary>How many tool rounds one message may take before the assistant stops and reports back.</summary>
    public const int MaxRounds = 40;

    private const string ServerFallbackBeta = "server-side-fallback-2026-07-01";

    private readonly AnthropicClient _client;
    private readonly AssistantTools _tools;
    private readonly IAssistantWorkspace _workspace;
    private readonly List<BetaMessageParam> _messages = [];
    private static readonly List<BetaToolUnion> Tools = BuildTools();

    /// <param name="apiKey">The user's key. Empty uses the key the SDK finds itself (ANTHROPIC_API_KEY, or a profile from `ant auth login`).</param>
    /// <param name="http">For tests: the HTTP client the requests go through.</param>
    public AssistantSession(string apiKey, IAssistantWorkspace workspace, HttpClient? http = null)
    {
        var key = apiKey.Trim();
        _client = (key.Length > 0, http) switch
        {
            (true, null) => new AnthropicClient { ApiKey = key },
            (true, { } h) => new AnthropicClient { ApiKey = key, HttpClient = h },
            (false, null) => new AnthropicClient(),
            (false, { } h) => new AnthropicClient { HttpClient = h },
        };
        _workspace = workspace;
        _tools = new AssistantTools(workspace);
    }

    public AssistantModel Model { get; set; } = AssistantModel.Default;

    /// <summary>How hard Claude thinks: low, medium, high, xhigh or max.</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Tokens used so far in this conversation, as the API counted them.</summary>
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CachedTokens { get; private set; }

    public bool IsEmpty => _messages.Count == 0;

    public static IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// Sends the user's message and runs the tool calls it leads to. Updates arrive as they happen. When the turn fails or is
    /// stopped, the conversation goes back to how it was before the message (changes already made to cards stay).
    /// </summary>
    public async Task Send(string text, Action<AssistantUpdate> update, CancellationToken cancel = default)
    {
        var start = _messages.Count;
        _messages.Add(new BetaMessageParam { Role = Role.User, Content = AssistantGuide.Context(_workspace) + text });
        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var response = await _client.Beta.Messages.Create(Request(), cancel);
                Count(response.Usage);
                if (response.StopReason == "refusal")
                {
                    // Nothing of a declined turn goes into the history, so the next message starts clean.
                    _messages.RemoveRange(start, _messages.Count - start);
                    update(new(AssistantUpdateKind.Problem, "Claude declined this request" + (response.StopDetails?.Explanation is { Length: > 0 } why ? $": {why}" : ".")));
                    return;
                }

                var (echo, calls) = Echo(response.Content);
                _messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = echo });
                foreach (var block in response.Content)
                    if (block.TryPickText(out var said) && said.Text.Trim().Length > 0) update(new(AssistantUpdateKind.Reply, said.Text.Trim()));

                if (calls.Count == 0)
                {
                    if (response.StopReason == "max_tokens") update(new(AssistantUpdateKind.Problem, "The reply was cut off because it got too long. Ask Claude to continue."));
                    return;
                }

                // One message with every tool's answer, in the order Claude asked.
                var results = new List<BetaContentBlockParam>();
                foreach (var call in calls)
                {
                    cancel.ThrowIfCancellationRequested();
                    var input = JsonSerializer.SerializeToElement(call.Input);
                    var outcome = _tools.Run(call.Name, input);
                    if (outcome.Activity is { } did) update(new(AssistantUpdateKind.Activity, did));
                    results.Add(new BetaToolResultBlockParam { ToolUseID = call.ID, Content = outcome.Content, IsError = outcome.IsError });
                }
                _messages.Add(new BetaMessageParam { Role = Role.User, Content = results });
            }
            update(new(AssistantUpdateKind.Problem, $"Claude used {MaxRounds} rounds of tools without finishing. Send another message to let it go on."));
            // The last message holds tool answers Claude hasn't read; a note lets the next message follow on from them.
            _messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = "I stopped after many steps; tell me to go on if you want me to continue." });
        }
        catch (Exception ex) when (ex is OperationCanceledException or AnthropicException or HttpRequestException or JsonException or InvalidOperationException or FormatException)
        {
            _messages.RemoveRange(start, _messages.Count - start);
            update(new(AssistantUpdateKind.Problem, Explain(ex)));
        }
    }

    public void Clear()
    {
        _messages.Clear();
        InputTokens = OutputTokens = CachedTokens = 0;
    }

    private MessageCreateParams Request() => new()
    {
        Model = Model.Id,
        MaxTokens = 16000,
        System = AssistantGuide.SystemPrompt,
        Tools = Tools,
        Messages = [.. _messages],
        OutputConfig = new BetaOutputConfig { Effort = Effort switch { "low" => Anthropic.Models.Beta.Messages.Effort.Low, "high" => Anthropic.Models.Beta.Messages.Effort.High, "xhigh" => Anthropic.Models.Beta.Messages.Effort.Xhigh, "max" => Anthropic.Models.Beta.Messages.Effort.Max, _ => Anthropic.Models.Beta.Messages.Effort.Medium } },
        // The guide, the tools and the conversation so far are the same on every request of a turn: cache them.
        CacheControl = new BetaCacheControlEphemeral(),
        // A request a safety classifier declines is re-run on the model Anthropic recommends for it.
        Betas = Model.ServerFallback ? [ServerFallbackBeta] : null,
        Fallbacks = Model.ServerFallback ? new BetaFallbacksParam(new Default()) : null,
    };

    /// <summary>
    /// Claude's reply as the next request must send it back: every block unchanged (thinking with its signature) and the
    /// tool calls it asks for.
    /// </summary>
    private static (List<BetaContentBlockParam> Echo, List<BetaToolUseBlock> Calls) Echo(IReadOnlyList<BetaContentBlock> content)
    {
        var echo = new List<BetaContentBlockParam>();
        var calls = new List<BetaToolUseBlock>();
        foreach (var block in content)
        {
            if (block.TryPickText(out var text)) echo.Add(new BetaTextBlockParam { Text = text.Text });
            else if (block.TryPickThinking(out var thinking)) echo.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            else if (block.TryPickRedactedThinking(out var redacted)) echo.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            else if (block.TryPickFallback(out var fallback)) echo.Add(new BetaFallbackBlockParam { From = new() { Model = fallback.From.Model }, To = new() { Model = fallback.To.Model } });
            else if (block.TryPickToolUse(out var call))
            {
                echo.Add(new BetaToolUseBlockParam { ID = call.ID, Name = call.Name, Input = call.Input });
                calls.Add(call);
            }
        }
        return (echo, calls);
    }

    private void Count(BetaUsage usage)
    {
        InputTokens += usage.InputTokens + (usage.CacheReadInputTokens ?? 0) + (usage.CacheCreationInputTokens ?? 0);
        OutputTokens += usage.OutputTokens;
        CachedTokens += usage.CacheReadInputTokens ?? 0;
    }

    private static List<BetaToolUnion> BuildTools() => AssistantTools.All.Select(t => (BetaToolUnion)new BetaTool
    {
        Name = t.Name,
        Description = t.Description,
        InputSchema = new()
        {
            Properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(t.InputSchema)!,
            Required = [.. t.Required],
        },
    }).ToList();

    /// <summary>What went wrong, in words the user can act on.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        OperationCanceledException => "Stopped.",
        AnthropicUnauthorizedException => "The API key was not accepted. Check that it is copied whole, and that it is still active in the Claude Console.",
        AnthropicForbiddenException => "This API key isn't allowed to use that model. Pick another model, or check the key's workspace in the Claude Console.",
        AnthropicRateLimitException => "Too many requests for now (rate limit), or the account is out of credit. Wait a moment, or check billing in the Claude Console.",
        AnthropicNotFoundException => "The model wasn't found. Pick another model.",
        AnthropicBadRequestException bad => $"The request was refused: {bad.Message}",
        Anthropic5xxException => "Anthropic's servers had a problem. Try again in a moment.",
        AnthropicIOException or HttpRequestException => "Couldn't reach api.anthropic.com. Check the internet connection (and any proxy or firewall).",
        _ => $"The service sent an answer Card Crafter didn't expect: {ex.Message}",
    };
}
