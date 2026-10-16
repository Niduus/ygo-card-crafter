namespace CardCrafter.Core.Assistant;

public enum ProviderKind
{
    /// <summary>Anthropic's Claude, through the official Anthropic SDK.</summary>
    Anthropic,
    /// <summary>Any service that speaks the OpenAI Chat Completions format with function calling.</summary>
    OpenAiCompatible,
}

/// <summary>
/// An AI service the assistant can talk to: where its API is, where users get a key, and which environment variable
/// holds one. Every service but Anthropic is reached through the OpenAI-compatible Chat Completions format.
/// </summary>
/// <param name="BaseUrl">The API root ("…/v1"), for OpenAI-compatible services.</param>
/// <param name="NeedsKey">False for servers on this computer (Ollama, LM Studio), which take no key.</param>
/// <param name="EditableUrl">The user can change the address (local servers, and any other compatible service).</param>
public sealed record AssistantProvider(
    string Id, string Name, ProviderKind Kind, string BaseUrl, string KeyPage, string? KeyVariable, string Note,
    bool NeedsKey = true, bool EditableUrl = false)
{
    public static IReadOnlyList<AssistantProvider> All { get; } =
    [
        new("anthropic", "Anthropic (Claude)", ProviderKind.Anthropic, "https://api.anthropic.com", "https://console.anthropic.com/settings/keys", "ANTHROPIC_API_KEY",
            "Recommended: the guide and tools were built and tested with Claude."),
        new("openai", "OpenAI", ProviderKind.OpenAiCompatible, "https://api.openai.com/v1", "https://platform.openai.com/api-keys", "OPENAI_API_KEY",
            "Pick a model that supports function calling (tools)."),
        new("gemini", "Google Gemini", ProviderKind.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai", "https://aistudio.google.com/apikey", "GEMINI_API_KEY",
            "Uses Gemini's OpenAI-compatible endpoint. Keys come from Google AI Studio."),
        new("openrouter", "OpenRouter", ProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1", "https://openrouter.ai/keys", "OPENROUTER_API_KEY",
            "One key for models from many companies. Pick one that supports tools."),
        new("xai", "xAI (Grok)", ProviderKind.OpenAiCompatible, "https://api.x.ai/v1", "https://console.x.ai", "XAI_API_KEY", ""),
        new("mistral", "Mistral", ProviderKind.OpenAiCompatible, "https://api.mistral.ai/v1", "https://console.mistral.ai/api-keys", "MISTRAL_API_KEY", ""),
        new("deepseek", "DeepSeek", ProviderKind.OpenAiCompatible, "https://api.deepseek.com/v1", "https://platform.deepseek.com/api_keys", "DEEPSEEK_API_KEY", ""),
        new("groq", "Groq", ProviderKind.OpenAiCompatible, "https://api.groq.com/openai/v1", "https://console.groq.com/keys", "GROQ_API_KEY",
            "Very fast open models. Pick one that supports tool use."),
        new("ollama", "Ollama (on this computer)", ProviderKind.OpenAiCompatible, "http://localhost:11434/v1", "https://ollama.com/search?c=tools", null,
            "Runs models on your own computer, free and offline. Start Ollama and pull a model that supports tools first.", NeedsKey: false, EditableUrl: true),
        new("lmstudio", "LM Studio (on this computer)", ProviderKind.OpenAiCompatible, "http://localhost:1234/v1", "https://lmstudio.ai", null,
            "Runs models on your own computer. Start LM Studio's local server with a model that supports tool use.", NeedsKey: false, EditableUrl: true),
        new("custom", "Other OpenAI-compatible service", ProviderKind.OpenAiCompatible, "", "", null,
            "Any service with an OpenAI-compatible Chat Completions API and tool calling. Enter its address (ending in /v1 or similar), and its key if it needs one.", NeedsKey: false, EditableUrl: true),
    ];

    public static AssistantProvider Default => All[0];

    public static AssistantProvider Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;

    public bool IsClaude => Kind == ProviderKind.Anthropic;

    public override string ToString() => Name;
}
