using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CardCrafter.App.Services;

namespace CardCrafter.App.Views;

public sealed class DialogService(Window owner) : IDialogService
{
    public async Task<string?> OpenFileAsync(string title, string filterName, params string[] extensions)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = extensions.Select(e => "*." + e).ToList() }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string extension)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = ["*." + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel") =>
        MessageWindow(title, message, yes, no).ShowDialog<bool>(owner);

    public Task ShowAsync(string title, string message) => MessageWindow(title, message, "OK", null).ShowDialog<bool>(owner);

    public async Task CopyToClipboardAsync(string text)
    {
        if (owner.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", path);
            else Process.Start("xdg-open", path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Opening a file manager is a convenience; the path is also shown in the status bar.
        }
    }

    private static Window MessageWindow(string title, string message, string yes, string? no)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yesButton = new Button { Content = yes, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        yesButton.Click += (_, _) => window.Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { yesButton } };
        if (no is not null)
        {
            var noButton = new Button { Content = no, IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            noButton.Click += (_, _) => window.Close(false);
            buttons.Children.Add(noButton);
        }
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons,
            },
        };
        return window;
    }
}
