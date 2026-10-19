using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Soundbord.Audio;
using Soundbord.Services;

namespace Soundbord.Views;

/// <summary>Avalonia-Umsetzung von <see cref="IDialogService"/>.</summary>
public sealed class DialogService(TopLevel topLevel) : IDialogService
{
    public async Task<IReadOnlyList<string>> PickAudioFilesAsync()
    {
        var patterns = AudioFormat.SupportedExtensions.Select(e => "*" + e).ToList();

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Sounds auswählen",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audiodateien") { Patterns = patterns },
                new FilePickerFileType("Alle Dateien") { Patterns = ["*"] },
            ],
        });

        return ToLocalPaths(files);
    }

    public async Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Ordner mit Sounds auswählen",
            AllowMultiple = true,
        });

        return ToLocalPaths(folders);
    }

    public void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
            }
            else
            {
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                    Process.Start(new ProcessStartInfo(parent) { UseShellExecute = true });
                }
            }
        }
        catch (Exception)
        {
            // Kein Explorer verfügbar – kein Grund, die App zu stören.
        }
    }

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* kein Standardbrowser gesetzt */ }
    }

    private static List<string> ToLocalPaths(IEnumerable<IStorageItem> items) =>
        items.Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
}
