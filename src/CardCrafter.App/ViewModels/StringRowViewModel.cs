using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>
/// One of the database's str1..str16, like DataEditorX's string section: the text the game shows for an effect in
/// its activation lists and "you can" prompts. Typing replaces the generated text; clearing it goes back.
/// </summary>
public sealed partial class StringRowViewModel : ObservableObject
{
    private readonly Action<int, string> _typed;
    private bool _updating;

    public StringRowViewModel(int index, string use, string generated, string text, Action<int, string> typed)
    {
        Index = index;
        _typed = typed;
        _use = use;
        _generated = generated;
        _text = text;
    }

    public int Index { get; }
    public string Label => $"str{Index + 1}";
    public string Lua => $"aux.Stringid(id,{Index})";

    [ObservableProperty] private string _use;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOverridden))] private string _generated;

    private string _text;
    /// <summary>The string the database gets.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value ?? "")) return;
            OnPropertyChanged(nameof(IsOverridden));
            if (!_updating) _typed(Index, _text == Generated ? "" : _text);
        }
    }

    public bool IsOverridden => Generated.Length > 0 && Text != Generated;

    /// <summary>Goes back to the generated text.</summary>
    [RelayCommand]
    private void Reset() => Text = Generated;

    /// <summary>Takes new values after a refresh without counting as typing.</summary>
    public void Update(string use, string generated, string text)
    {
        _updating = true;
        try
        {
            Use = use;
            Generated = generated;
            if (text != _text) Text = text;
            OnPropertyChanged(nameof(IsOverridden));
        }
        finally { _updating = false; }
    }
}
