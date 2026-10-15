namespace CardCrafter.App.Services;

/// <summary>File pickers and message boxes, so view models stay free of UI code.</summary>
public interface IDialogService
{
    Task<string?> OpenFileAsync(string title, string filterName, params string[] extensions);
    Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string extension);
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel");
    Task ShowAsync(string title, string message);
    Task CopyToClipboardAsync(string text);
    void OpenFolder(string path);
    void OpenUrl(string url);
}
