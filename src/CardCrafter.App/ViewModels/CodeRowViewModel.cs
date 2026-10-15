using CardCrafter.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>One code of a card (passcode, Type, archetype...) with a button that copies it.</summary>
public sealed partial class CodeRowViewModel(CardCode code) : ObservableObject
{
    /// <summary>Copies text to the clipboard; set by the app at start.</summary>
    public static Func<string, Task>? Copier { get; set; }

    public CardCode Code { get; } = code;
    public string Label => Code.Label;
    public string Value => Code.Value;
    public string Note => Code.Note;
    public bool HasNote => Code.Note.Length > 0;

    [ObservableProperty] private string _copyLabel = "Copy";

    [RelayCommand]
    private async Task Copy()
    {
        if (Copier is null) return;
        await Copier(Code.Value);
        CopyLabel = "Copied";
    }

    public static List<CodeRowViewModel> Rows(IEnumerable<CardCode> codes) => codes.Select(c => new CodeRowViewModel(c)).ToList();

    /// <summary>Every code as "Label: value (note)" lines, for "Copy all".</summary>
    public static string AllText(IEnumerable<CodeRowViewModel> rows) =>
        string.Join(Environment.NewLine, rows.Select(r => $"{r.Label}: {r.Value}{(r.HasNote ? $" ({r.Note})" : "")}"));
}
