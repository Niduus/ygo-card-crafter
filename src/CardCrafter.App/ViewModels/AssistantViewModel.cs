using System.Collections.ObjectModel;
using System.Globalization;
using CardCrafter.App.Services;
using CardCrafter.Core.Assistant;
using CardCrafter.Core.Omega;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>One line of the chat.</summary>
public sealed record ChatLine(ChatRole Role, string Text)
{
    public bool IsYou => Role == ChatRole.You;
    public bool IsClaude => Role == ChatRole.Claude;
    public bool IsActivity => Role == ChatRole.Activity;
    public bool IsProblem => Role == ChatRole.Problem;
    public string Who => Role switch { ChatRole.You => "You", ChatRole.Claude => "Claude", ChatRole.Activity => "", _ => "" };
    public string Shown => Role == ChatRole.Activity ? "✎ " + Text : Text;
}

public enum ChatRole { You, Claude, Activity, Problem }

/// <summary>
/// The Assistant tab: the user's Claude API key and settings, and a chat with Claude that can read and change the pack's
/// cards. Each message that changes cards can be undone as a whole.
/// </summary>
public sealed partial class AssistantViewModel : ObservableObject
{
    public const string KeysPage = "https://console.anthropic.com/settings/keys";
    public const string GuidePage = "https://github.com/Niduus/ygo-card-crafter/blob/main/docs/ai-assistant.md";

    private readonly MainViewModel _main;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Stack<CardsSnapshot> _undo = new();
    private AssistantSession? _session;
    private string _sessionKey = "";
    private CancellationTokenSource? _cancel;

    public AssistantViewModel(MainViewModel main, AppSettings settings, IDialogService dialogs)
    {
        _main = main;
        _settings = settings;
        _dialogs = dialogs;
        var stored = ApiKeyStore.Load();
        _apiKey = stored ?? "";
        _rememberKey = stored is not null;
        _selectedModel = AssistantModel.Find(settings.AssistantModel);
        _selectedEffort = AssistantSession.Efforts.Contains(settings.AssistantEffort) ? settings.AssistantEffort : "medium";
    }

    /// <summary>The "How it works" panel: what the key is for, where it goes, and what Claude sees and can do.</summary>
    public static string HowItWorks { get; } = string.Join("\n\n",
    [
        "Your key, your account. Make a key in the Claude Console (Settings → API keys). What Claude uses is billed to that account at Anthropic's API prices; the token count under the chat shows how much this chat used.",
        "The key stays on this computer. It is sent only to api.anthropic.com, and never written into a pack or an export.",
        "What Claude sees: your messages, Card Crafter's guide to its blocks (the same for everyone), and the cards it reads with its tools — the selected card first, others in the pack only when it looks them up. Nothing else on your computer.",
        "What Claude can do: read and change the open pack's cards through the same blocks, card pickers and card fields you use. Every change appears in the editor at once and each one is listed in the chat. \"Undo Claude's last changes\" puts the cards back. Nothing is saved until you save the pack.",
        "How it builds: it looks up blocks and their options, places them, and reads back the card text and problems the app finds, the same ones you see on the Effects tab, then fixes what is wrong. For wording it isn't sure of, it can read an official card's script with the translator to see which blocks the app uses for it.",
    ]);

    public ObservableCollection<ChatLine> Lines { get; } = [];
    public IReadOnlyList<AssistantModel> Models => AssistantModel.All;
    public IReadOnlyList<string> Efforts => AssistantSession.Efforts;

    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private bool _rememberKey;
    [ObservableProperty] private AssistantModel _selectedModel;
    [ObservableProperty] private string _selectedEffort;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _input = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(UndoCommand), nameof(NewChatCommand))] private bool _isBusy;
    [ObservableProperty] private string _usage = "";

    /// <summary>A key in the box, or one the SDK finds by itself in ANTHROPIC_API_KEY.</summary>
    public bool HasKey => ApiKey.Trim().Length > 0 || Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 };
    public string KeyStatus => ApiKey.Trim().Length > 0
        ? RememberKey ? "Key saved on this computer." : "Key kept until Card Crafter closes."
        : HasKey ? "Using the key in the ANTHROPIC_API_KEY environment variable." : "No key yet.";
    public bool IsEmpty => Lines.Count == 0;
    public bool CanUndoChanges => _undo.Count > 0;

    partial void OnApiKeyChanged(string value)
    {
        if (RememberKey) SaveKey();
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(KeyStatus));
    }

    partial void OnRememberKeyChanged(bool value)
    {
        if (value) SaveKey();
        else ApiKeyStore.Forget();
        OnPropertyChanged(nameof(KeyStatus));
    }

    private void SaveKey()
    {
        if (ApiKey.Trim().Length > 0) ApiKeyStore.Save(ApiKey.Trim());
        else ApiKeyStore.Forget();
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

    private bool CanSend() => !IsBusy && Input.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Input.Trim();
        if (!HasKey)
        {
            Add(ChatRole.Problem, "Paste your Claude API key above first. \"Get a key\" opens the page where you make one.");
            return;
        }
        Input = "";
        Add(ChatRole.You, text);
        var key = ApiKey.Trim();
        if (_session is null || _sessionKey != key)
        {
            // A new key starts a new conversation; the old one belonged to the other key's account.
            _session = new AssistantSession(key, _main);
            _sessionKey = key;
        }
        _session.Model = SelectedModel;
        _session.Effort = SelectedEffort;

        var before = _main.SnapshotCards();
        var changed = false;
        IsBusy = true;
        _cancel = new CancellationTokenSource();
        try
        {
            await _session.Send(text, update =>
            {
                if (update.Kind == AssistantUpdateKind.Activity) changed = true;
                Add(update.Kind switch { AssistantUpdateKind.Reply => ChatRole.Claude, AssistantUpdateKind.Activity => ChatRole.Activity, _ => ChatRole.Problem }, update.Text);
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
            OnPropertyChanged(nameof(CanUndoChanges));
            UndoCommand.NotifyCanExecuteChanged();
        }
        Usage = string.Format(CultureInfo.CurrentCulture, "This chat so far: {0:N0} tokens sent ({1:N0} of them from the cache), {2:N0} received.",
            _session.InputTokens, _session.CachedTokens, _session.OutputTokens);
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cancel?.Cancel();

    private bool CanUndo() => !IsBusy && _undo.Count > 0;

    /// <summary>Puts the cards back as they were before Claude's last message that changed them.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var snapshot = _undo.Pop();
        Add(ChatRole.Activity, _main.RestoreCards(snapshot)
            ? "Undid Claude's changes from that message."
            : "That message's changes were in a pack that is no longer open.");
        OnPropertyChanged(nameof(CanUndoChanges));
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartOver() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartOver))]
    private void NewChat()
    {
        _session?.Clear();
        Lines.Clear();
        Usage = "";
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void ForgetKey()
    {
        RememberKey = false;
        ApiKey = "";
        ApiKeyStore.Forget();
    }

    [RelayCommand]
    private void GetKey() => _dialogs.OpenUrl(KeysPage);

    [RelayCommand]
    private void OpenGuide() => _dialogs.OpenUrl(GuidePage);

    /// <summary>Puts a starter question in the box.</summary>
    [RelayCommand]
    private void Suggest(string text) => Input = text;

    private void Add(ChatRole role, string text)
    {
        Lines.Add(new ChatLine(role, text));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
