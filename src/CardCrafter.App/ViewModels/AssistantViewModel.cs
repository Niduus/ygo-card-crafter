using System.Collections.ObjectModel;
using System.Globalization;
using CardCrafter.App.Services;
using CardCrafter.Core.Assistant;
using CardCrafter.Core.Omega;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>One line of the chat.</summary>
public sealed record ChatLine(ChatRole Role, string Text, string Who = "")
{
    public bool IsYou => Role == ChatRole.You;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsActivity => Role == ChatRole.Activity;
    public bool IsProblem => Role == ChatRole.Problem;
    public string Shown => Role == ChatRole.Activity ? "✎ " + Text : Text;
}

public enum ChatRole { You, Assistant, Activity, Problem }

/// <summary>
/// The Assistant tab: which AI service to use with the user's own key, and a chat with it that can read and change the
/// pack's cards. Claude goes through Anthropic's SDK; every other service through the OpenAI-compatible format. Each message
/// that changes cards can be undone as a whole.
/// </summary>
public sealed partial class AssistantViewModel : ObservableObject
{
    public const string GuidePage = "https://github.com/Niduus/ygo-card-crafter/blob/main/docs/ai-assistant.md";

    private readonly MainViewModel _main;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Stack<CardsSnapshot> _undo = new();
    private IAssistantChat? _chat;
    private string _chatFor = "";
    private CancellationTokenSource? _cancel;
    private bool _loading;

    public AssistantViewModel(MainViewModel main, AppSettings settings, IDialogService dialogs)
    {
        _main = main;
        _settings = settings;
        _dialogs = dialogs;
        _selectedModel = AssistantModel.Find(settings.AssistantModel);
        _selectedEffort = AssistantSession.Efforts.Contains(settings.AssistantEffort) ? settings.AssistantEffort : "medium";
        _selectedProvider = AssistantProvider.Find(settings.AssistantProvider);
        LoadProvider();
    }

    /// <summary>The "How it works" panel: what the key is for, where it goes, and what the assistant sees and can do.</summary>
    public string HowItWorks => string.Join("\n\n",
    [
        SelectedProvider.Id is not ("ollama" or "lmstudio")
            ? $"Your key, your account. Make a key at {SelectedProvider.Name}; what the assistant uses is billed to that account at its API prices. The count under the chat shows the tokens this chat used."
            : "This runs on your own computer (or a server you choose): nothing is billed, and nothing leaves the machine it runs on.",
        $"The key stays on this computer. It is sent only to {Host}, and never written into a pack or an export.",
        "What the assistant sees: your messages, Card Crafter's guide to its blocks (the same for everyone), and the cards it reads with its tools: the selected card first, others in the pack only when it looks them up. Nothing else on your computer.",
        "What it can do: read and change the open pack's cards through the same blocks, card pickers and card fields you use. Every change appears in the editor at once and each one is listed in the chat. \"Undo the last changes\" puts the cards back. Nothing is saved until you save the pack.",
        "How it builds: it looks up blocks and their options, places them, and reads back the card text and problems the app finds, the same ones you see on the Effects tab, then fixes what is wrong. For wording it isn't sure of, it can read an official card's script with the translator to see which blocks the app uses for it.",
        "Any service works if its model supports tools (function calling). The guide and tools were built and tested with Claude; other models may need more guidance.",
    ]);

    private string Host => Uri.TryCreate(BaseUrl.Length > 0 ? BaseUrl : SelectedProvider.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : "the service's address";

    public ObservableCollection<ChatLine> Lines { get; } = [];
    public IReadOnlyList<AssistantProvider> Providers => AssistantProvider.All;
    public IReadOnlyList<AssistantModel> Models => AssistantModel.All;
    public IReadOnlyList<string> Efforts => AssistantSession.Efforts;
    public ObservableCollection<string> ModelSuggestions { get; } = [];

    [ObservableProperty] private AssistantProvider _selectedProvider;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _rememberKey;
    [ObservableProperty] private AssistantModel _selectedModel;
    [ObservableProperty] private string _selectedEffort;
    /// <summary>The model for a provider other than Anthropic, as the service names it.</summary>
    [ObservableProperty] private string _modelName = "";
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _modelsStatus = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _input = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(UndoCommand), nameof(NewChatCommand))] private bool _isBusy;
    [ObservableProperty] private string _usage = "";

    public bool IsClaude => SelectedProvider.IsClaude;
    public bool IsOther => !SelectedProvider.IsClaude;
    public bool UrlEditable => SelectedProvider.EditableUrl;
    public bool HasKeyPage => SelectedProvider.KeyPage.Length > 0;
    public string ProviderNote => SelectedProvider.Note;
    public bool HasProviderNote => ProviderNote.Length > 0;
    public string KeyLabel => SelectedProvider.NeedsKey ? $"{SelectedProvider.Name} API key" : "API key (only if the server needs one)";
    public string KeyPageLabel => SelectedProvider.NeedsKey ? "Get a key" : "Get models";
    /// <summary>What the assistant's lines are headed with.</summary>
    public string AssistantName => IsClaude ? "Claude" : ModelName.Trim().Length > 0 ? ModelName.Trim() : "Assistant";

    private string? EnvironmentKey => SelectedProvider.KeyVariable is { } v && Environment.GetEnvironmentVariable(v) is { Length: > 0 } k ? k : null;

    /// <summary>A key in the box, one in the provider's environment variable, or a service that needs none.</summary>
    public bool HasKey => ApiKey.Trim().Length > 0 || EnvironmentKey is not null || !SelectedProvider.NeedsKey;
    public string KeyStatus => ApiKey.Trim().Length > 0
        ? RememberKey ? "Key saved on this computer." : "Key kept until Card Crafter closes."
        : EnvironmentKey is not null ? $"Using the key in the {SelectedProvider.KeyVariable} environment variable."
        : SelectedProvider.NeedsKey ? "No key yet." : "No key needed.";
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Shows the chosen provider's key, model and address.</summary>
    private void LoadProvider()
    {
        _loading = true;
        try
        {
            var p = SelectedProvider;
            var stored = ApiKeyStore.Load(p.Id);
            ApiKey = stored ?? "";
            RememberKey = stored is not null;
            ModelName = _settings.AssistantModels.GetValueOrDefault(p.Id, "");
            BaseUrl = _settings.AssistantUrls.GetValueOrDefault(p.Id, p.BaseUrl);
            ModelSuggestions.Clear();
            ModelsStatus = "";
        }
        finally
        {
            _loading = false;
        }
        foreach (var name in new[] { nameof(IsClaude), nameof(IsOther), nameof(UrlEditable), nameof(HasKeyPage), nameof(ProviderNote), nameof(HasProviderNote),
                     nameof(KeyLabel), nameof(KeyPageLabel), nameof(AssistantName), nameof(HasKey), nameof(KeyStatus), nameof(HowItWorks) })
            OnPropertyChanged(name);
    }

    partial void OnSelectedProviderChanged(AssistantProvider value)
    {
        _settings.AssistantProvider = value.Id;
        _settings.Save();
        LoadProvider();
    }

    partial void OnApiKeyChanged(string value)
    {
        if (!_loading && RememberKey) SaveKey();
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(KeyStatus));
    }

    partial void OnRememberKeyChanged(bool value)
    {
        if (_loading) return;
        if (value) SaveKey();
        else ApiKeyStore.Forget(SelectedProvider.Id);
        OnPropertyChanged(nameof(KeyStatus));
    }

    private void SaveKey()
    {
        if (ApiKey.Trim().Length > 0) ApiKeyStore.Save(ApiKey.Trim(), SelectedProvider.Id);
        else ApiKeyStore.Forget(SelectedProvider.Id);
    }

    partial void OnModelNameChanged(string value)
    {
        OnPropertyChanged(nameof(AssistantName));
        if (_loading) return;
        _settings.AssistantModels[SelectedProvider.Id] = value.Trim();
        _settings.Save();
    }

    partial void OnBaseUrlChanged(string value)
    {
        OnPropertyChanged(nameof(HowItWorks));
        if (_loading || !SelectedProvider.EditableUrl) return;
        _settings.AssistantUrls[SelectedProvider.Id] = value.Trim();
        _settings.Save();
    }

    partial void OnSelectedModelChanged(AssistantModel value)
    {
        _settings.AssistantModel = value.Id;
        _settings.Save();
    }

    partial void OnSelectedEffortChanged(string value)
    {
        _settings.AssistantEffort = value;
        _settings.Save();
    }

    private string Key => ApiKey.Trim().Length > 0 ? ApiKey.Trim() : EnvironmentKey ?? "";

    /// <summary>The conversation for the current provider, key and address; changing any of them starts a new one.</summary>
    private IAssistantChat Chat()
    {
        var p = SelectedProvider;
        var url = p.EditableUrl ? BaseUrl : p.BaseUrl;
        var id = $"{p.Id}|{Key}|{url}";
        if (_chat is null || _chatFor != id)
        {
            _chat = p.IsClaude ? new AssistantSession(ApiKey.Trim(), _main) : new OpenAiCompatibleSession(p, url, Key, _main);
            _chatFor = id;
        }
        switch (_chat)
        {
            case AssistantSession claude:
                claude.Model = SelectedModel;
                claude.Effort = SelectedEffort;
                break;
            case OpenAiCompatibleSession other:
                other.Model = ModelName;
                break;
        }
        return _chat;
    }

    private bool CanSend() => !IsBusy && Input.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Input.Trim();
        if (!HasKey)
        {
            Add(ChatRole.Problem, $"Paste your {SelectedProvider.Name} API key on the right first. \"Get a key\" opens the page where you make one.");
            return;
        }
        Input = "";
        Add(ChatRole.You, text);
        var chat = Chat();
        var before = _main.SnapshotCards();
        var changed = false;
        var who = AssistantName;
        IsBusy = true;
        _cancel = new CancellationTokenSource();
        try
        {
            await chat.Send(text, update =>
            {
                if (update.Kind == AssistantUpdateKind.Activity) changed = true;
                Add(update.Kind switch { AssistantUpdateKind.Reply => ChatRole.Assistant, AssistantUpdateKind.Activity => ChatRole.Activity, _ => ChatRole.Problem }, update.Text, who);
            }, _cancel.Token);
        }
        finally
        {
            IsBusy = false;
            _cancel.Dispose();
            _cancel = null;
        }
        if (changed)
        {
            _undo.Push(before);
            UndoCommand.NotifyCanExecuteChanged();
        }
        Usage = string.Format(CultureInfo.CurrentCulture, "This chat so far: {0:N0} tokens sent ({1:N0} of them from the cache), {2:N0} received.",
            chat.InputTokens, chat.CachedTokens, chat.OutputTokens);
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cancel?.Cancel();

    private bool CanUndo() => !IsBusy && _undo.Count > 0;

    /// <summary>Puts the cards back as they were before the assistant's last message that changed them.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var snapshot = _undo.Pop();
        Add(ChatRole.Activity, _main.RestoreCards(snapshot)
            ? "Undid the changes from that message."
            : "That message's changes were in a pack that is no longer open.");
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartOver() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartOver))]
    private void NewChat()
    {
        _chat?.Clear();
        Lines.Clear();
        Usage = "";
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void ForgetKey()
    {
        RememberKey = false;
        ApiKey = "";
        ApiKeyStore.Forget(SelectedProvider.Id);
    }

    [RelayCommand]
    private void GetKey()
    {
        if (SelectedProvider.KeyPage.Length > 0) _dialogs.OpenUrl(SelectedProvider.KeyPage);
    }

    /// <summary>Asks the service which models it has, for the model box.</summary>
    [RelayCommand]
    private async Task LoadModels()
    {
        ModelsStatus = "Asking the service…";
        try
        {
            var models = await OpenAiCompatibleSession.ListModels(SelectedProvider.EditableUrl ? BaseUrl : SelectedProvider.BaseUrl, Key);
            ModelSuggestions.Clear();
            foreach (var m in models) ModelSuggestions.Add(m);
            ModelsStatus = models.Count == 0 ? "The service listed no models." : $"{models.Count} models; start typing to pick one.";
            if (ModelName.Trim().Length == 0 && models.Count > 0) ModelName = models[0];
        }
        catch (Exception ex) when (ex is HttpRequestException or ProviderException or TaskCanceledException or System.Text.Json.JsonException or UriFormatException or InvalidOperationException)
        {
            ModelsStatus = OpenAiCompatibleSession.Explain(ex);
        }
    }

    [RelayCommand]
    private void OpenGuide() => _dialogs.OpenUrl(GuidePage);

    /// <summary>Puts a starter question in the box.</summary>
    [RelayCommand]
    private void Suggest(string text) => Input = text;

    private void Add(ChatRole role, string text, string who = "")
    {
        Lines.Add(new ChatLine(role, text, who));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
