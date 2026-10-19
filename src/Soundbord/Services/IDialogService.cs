namespace Soundbord.Services;

/// <summary>
/// Alles, wofür ein ViewModel ein Fenster bräuchte. Hält die ViewModels frei von
/// Avalonia-Typen und damit testbar.
/// </summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> PickAudioFilesAsync();

    Task<IReadOnlyList<string>> PickFoldersAsync();

    void RevealInExplorer(string path);

    void OpenUrl(string url);
}
