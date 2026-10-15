using Avalonia;
using Avalonia.Headless;
using CardCrafter.App.Services;

[assembly: AvaloniaTestApplication(typeof(CardCrafter.App.Tests.TestApp))]

namespace CardCrafter.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Answers every dialog without showing anything.</summary>
public sealed class FakeDialogs : IDialogService
{
    public string? NextFile { get; set; }
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Shown { get; } = [];

    public Task<string?> OpenFileAsync(string title, string filterName, params string[] extensions) => Task.FromResult(NextFile);
    public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string extension) => Task.FromResult(NextFile);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(NextFile);
    public Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel") => Task.FromResult(ConfirmAnswer);
    public Task ShowAsync(string title, string message) { Shown.Add(message); return Task.CompletedTask; }
    public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
    public void OpenFolder(string path) { }
}
