using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundbord.Audio;
using Soundbord.Models;
using Soundbord.Services;

namespace Soundbord.ViewModels;

public partial class SoundboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private readonly Action _bindingsChanged;

    private readonly List<SoundItemViewModel> _allSounds = [];
    private readonly Dictionary<Guid, SoundItemViewModel> _soundsById = [];
    private readonly List<FolderNodeViewModel> _allFolders = [];
    private readonly Dictionary<Guid, FolderNodeViewModel> _foldersById = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty] private object? _selectedNode;

    public SoundboardViewModel(AppServices services, IDialogService dialogs, Action bindingsChanged)
    {
        _services = services;
        _dialogs = dialogs;
        _bindingsChanged = bindingsChanged;

        foreach (var entry in services.Folders)
        {
            var node = new FolderNodeViewModel(entry, this);
            _allFolders.Add(node);
            _foldersById[entry.Id] = node;
        }

        foreach (var entry in services.Sounds)
        {
            var item = new SoundItemViewModel(entry, this);
            _allSounds.Add(item);
            _soundsById[entry.Id] = item;
        }

        ApplyView();
    }

    private readonly List<object> _rootNodes = [];

    /// <summary>
    /// Der Baum als flache Liste, ausgerückt über <c>Depth</c>.
    ///
    /// Bewusst kein <c>TreeView</c>: dessen Aufklappzustand müsste an eine
    /// Eigenschaft gebunden werden, die Ordner haben und Sounds nicht – das
    /// erzeugt bei jedem Blatt einen Bindungsfehler. Eine flache Liste umgeht
    /// das vollständig und macht Drag &amp; Drop obendrein eindeutiger.
    /// </summary>
    public ObservableCollection<object> VisibleItems { get; } = [];

    public IReadOnlyList<SoundItemViewModel> All => _allSounds;

    public bool IsSearching => SearchText.Trim().Length > 0;

    public bool ShowEmptyState => _allSounds.Count == 0 && _allFolders.Count == 0;

    public bool ShowNoResults => IsSearching && VisibleItems.Count == 0;

    public string CountText
    {
        get
        {
            var sounds = _allSounds.Count == 1 ? "1 Sound" : $"{_allSounds.Count} Sounds";
            if (_allFolders.Count == 0) return sounds;
            var folders = _allFolders.Count == 1 ? "1 Ordner" : $"{_allFolders.Count} Ordner";
            return $"{sounds} · {folders}";
        }
    }

    // ================= Wiedergabe =================

    public void Toggle(SoundItemViewModel item)
    {
        if (item.IsMissing)
        {
            item.RefreshMissing();
            if (item.IsMissing)
            {
                return;
            }
        }

        // Nicht im UI-Thread abspielen: ein Cache-Fehltreffer müsste sonst hier
        // dekodieren und würde die Oberfläche für Millisekunden einfrieren.
        _services.Triggers.Post(() => _services.Engine.Toggle(item.Entry));
    }

    public void SetPlayingState(Guid soundId, bool playing)
    {
        if (_soundsById.TryGetValue(soundId, out var item)) item.IsPlaying = playing;
    }

    [RelayCommand]
    private void StopAll() => _services.Triggers.Post(() => _services.Engine.StopAll());

    public void OnItemVolumeChanged(SoundItemViewModel item)
    {
        _services.Engine.SetSoundVolume(item.Id, (float)item.Volume);
        PersistLibrary();
    }

    // ================= Hotkeys =================

    public void BeginHotkeyCapture(SoundItemViewModel item)
    {
        if (item.IsRecordingHotkey)
        {
            _services.Recorder.Cancel();
            return;
        }

        item.IsRecordingHotkey = true;

        _services.Recorder.Begin(hotkey =>
        {
            item.IsRecordingHotkey = false;
            if (hotkey is null) return;

            // Ein Kürzel gehört immer nur einem Sound. Wer es neu vergibt, nimmt
            // es dem bisherigen Besitzer weg – das ist erwartbarer als eine
            // Fehlermeldung, die den Nutzer raten lässt, wo es schon belegt ist.
            var previous = _allSounds.FirstOrDefault(o => o != item && hotkey.Equals(o.Entry.Hotkey));
            previous?.SetHotkey(null);

            item.SetHotkey(hotkey);
            PersistLibrary();
            RefreshHotkeyBindings();
        });
    }

    public void RefreshHotkeyBindings() => _bindingsChanged();

    // ================= Ordner =================

    [RelayCommand]
    private void AddRootFolder() => AddFolder(null);

    public void AddFolder(FolderNodeViewModel? parent)
    {
        var entry = new FolderEntry
        {
            Name = UniqueFolderName(parent?.Id),
            ParentId = parent?.Id,
            Order = NextFolderOrder(parent?.Id),
        };

        var node = new FolderNodeViewModel(entry, this);
        _services.Folders.Add(entry);
        _allFolders.Add(node);
        _foldersById[entry.Id] = node;

        if (parent is not null) parent.IsExpanded = true;

        ApplyView();
        PersistLibrary();
        SelectedNode = node;
    }

    /// <summary>
    /// Löscht den Ordner, aber nichts von seinem Inhalt: Sounds und Unterordner
    /// rücken eine Ebene nach oben. Ein Klick darf keine Bibliothek vernichten.
    /// </summary>
    public void RemoveFolder(FolderNodeViewModel folder)
    {
        var parentId = folder.Entry.ParentId;

        foreach (var sound in _allSounds.Where(s => s.Entry.FolderId == folder.Id))
            sound.Entry.FolderId = parentId;

        foreach (var child in _allFolders.Where(f => f.Entry.ParentId == folder.Id))
            child.Entry.ParentId = parentId;

        _allFolders.Remove(folder);
        _foldersById.Remove(folder.Id);
        _services.Folders.RemoveAll(f => f.Id == folder.Id);

        ApplyView();
        PersistLibrary();
    }

    // ================= Verschieben =================

    public void MoveSound(SoundItemViewModel sound, Guid? targetFolderId)
    {
        if (sound.Entry.FolderId == targetFolderId) return;

        sound.Entry.FolderId = targetFolderId;
        sound.Entry.Order = NextSoundOrder(targetFolderId);

        // Zielordner aufklappen, damit der verschobene Sound sichtbar bleibt.
        if (targetFolderId is { } id && _foldersById.TryGetValue(id, out var folder))
            folder.IsExpanded = true;

        ApplyView();
        PersistLibrary();
    }

    public bool CanMoveFolder(FolderNodeViewModel folder, Guid? targetParentId)
    {
        if (targetParentId == folder.Id) return false;
        if (folder.Entry.ParentId == targetParentId) return false;

        // Ein Ordner darf nicht in seinen eigenen Nachfahren wandern – der Baum
        // hinge sonst in einer Schleife und der Zweig wäre unerreichbar.
        var current = targetParentId;
        while (current is { } id && _foldersById.TryGetValue(id, out var node))
        {
            if (node.Id == folder.Id) return false;
            current = node.Entry.ParentId;
        }

        return true;
    }

    public void MoveFolder(FolderNodeViewModel folder, Guid? targetParentId)
    {
        if (!CanMoveFolder(folder, targetParentId)) return;

        folder.Entry.ParentId = targetParentId;
        folder.Entry.Order = NextFolderOrder(targetParentId);

        // Zielordner aufklappen, damit der verschobene Ordner sichtbar bleibt.
        if (targetParentId is { } id && _foldersById.TryGetValue(id, out var parent))
            parent.IsExpanded = true;

        ApplyView();
        PersistLibrary();
    }

    // ================= Bibliothek =================

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        var files = await _dialogs.PickAudioFilesAsync();
        await ImportAsync(files);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var folders = await _dialogs.PickFoldersAsync();
        await ImportAsync(folders, mirrorStructure: true);
    }

    /// <summary>
    /// Import aus Dialog oder Drag &amp; Drop.
    /// </summary>
    /// <param name="mirrorStructure">
    /// Bei ausgewählten Ordnern deren Struktur als Ordner in der Bibliothek nachbilden,
    /// statt alles auf einen Haufen zu werfen.
    /// </param>
    public async Task ImportAsync(IEnumerable<string> paths, bool mirrorStructure = true)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;

        IsBusy = true;

        try
        {
            var target = SelectedNode as FolderNodeViewModel;
            var expanded = await Task.Run(() => ExpandForImport(list, mirrorStructure));

            var existing = _allSounds
                .Select(i => i.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = new List<SoundItemViewModel>();
            var createdFolders = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in expanded)
            {
                var full = Path.GetFullPath(item.FilePath);
                if (!existing.Add(full)) continue;

                var folderId = item.RelativeFolder is null
                    ? target?.Id
                    : EnsureFolderPath(item.RelativeFolder, target?.Id, createdFolders);

                var entry = SoundLibrary.CreateEntry(full);
                entry.FolderId = folderId;
                entry.Order = NextSoundOrder(folderId);

                var vm = new SoundItemViewModel(entry, this);
                _services.Sounds.Add(entry);
                _allSounds.Add(vm);
                _soundsById[entry.Id] = vm;
                added.Add(vm);
            }

            ApplyView();
            PersistLibrary();


            if (added.Count > 0) _ = WarmUpAsync(added);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private sealed record ImportItem(string FilePath, string? RelativeFolder);

    /// <summary>Löst Dateien und Ordner auf und merkt sich dabei die Ordnerstruktur.</summary>
    private static List<ImportItem> ExpandForImport(IReadOnlyList<string> paths, bool mirrorStructure)
    {
        var result = new List<ImportItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                    var rootName = Path.GetFileName(root);

                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!AudioFormat.IsSupported(file)) continue;
                        if (!seen.Add(Path.GetFullPath(file))) continue;

                        string? relative = null;
                        if (mirrorStructure)
                        {
                            var directory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? root;
                            var suffix = directory.Length > root.Length
                                ? directory[(root.Length + 1)..]
                                : string.Empty;
                            relative = suffix.Length == 0 ? rootName : Path.Combine(rootName, suffix);
                        }

                        result.Add(new ImportItem(file, relative));
                    }
                }
                else if (File.Exists(path) && AudioFormat.IsSupported(path) && seen.Add(Path.GetFullPath(path)))
                {
                    result.Add(new ImportItem(path, null));
                }
            }
            catch (Exception)
            {
                // Unlesbare Ordner (Rechte, defekte Verknüpfungen) einfach überspringen.
            }
        }

        result.Sort((a, b) => string.Compare(
            Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath), StringComparison.CurrentCultureIgnoreCase));

        return result;
    }

    /// <summary>Legt die Ordner eines relativen Pfades an, soweit sie noch fehlen.</summary>
    private Guid EnsureFolderPath(string relative, Guid? parentId, Dictionary<string, Guid> cache)
    {
        var current = parentId;
        var key = string.Empty;

        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;

            key = key.Length == 0 ? part : key + "/" + part;
            if (cache.TryGetValue(key, out var known))
            {
                current = known;
                continue;
            }

            var existing = _allFolders.FirstOrDefault(f =>
                f.Entry.ParentId == current &&
                string.Equals(f.Name, part, StringComparison.CurrentCultureIgnoreCase));

            if (existing is not null)
            {
                current = existing.Id;
                cache[key] = existing.Id;
                continue;
            }

            var entry = new FolderEntry
            {
                Name = part,
                ParentId = current,
                Order = NextFolderOrder(current),
            };

            var node = new FolderNodeViewModel(entry, this);
            _services.Folders.Add(entry);
            _allFolders.Add(node);
            _foldersById[entry.Id] = node;

            cache[key] = entry.Id;
            current = entry.Id;
        }

        return current ?? Guid.Empty;
    }

    public void Remove(SoundItemViewModel item)
    {
        _services.Engine.Stop(item.Id);
        _services.Engine.Cache.Remove(item.FilePath);

        _allSounds.Remove(item);
        _soundsById.Remove(item.Id);
        _services.Sounds.RemoveAll(s => s.Id == item.Id);

        ApplyView();
        PersistLibrary();
        RefreshHotkeyBindings();
    }

    public void ShowInFolder(SoundItemViewModel item) => _dialogs.RevealInExplorer(item.FilePath);

    public void PersistLibrary() => _services.SaveLibrarySoon();

    // ================= Ansicht aufbauen =================

    partial void OnSearchTextChanged(string value) => ApplyView();

    /// <summary>Wird nach jeder Änderung an Struktur, Suche oder Aufklappzustand aufgerufen.</summary>
    public void ApplyView()
    {
        VisibleItems.Clear();

        if (IsSearching)
        {
            var query = SearchText.Trim();

            // Im Suchmodus zählt Finden, nicht Struktur: eine flache Trefferliste
            // erspart das Aufklappen von Ordnern.
            foreach (var item in _allSounds
                         .Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                     i.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                         .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                item.Depth = 0;
                VisibleItems.Add(item);
            }
        }
        else
        {
            BuildTree();
            Flatten(_rootNodes, 0);
        }

        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(CountText));
    }

    private void BuildTree()
    {
        var folders = _allFolders
            .OrderBy(f => f.Entry.Order)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var folder in folders) folder.Children.Clear();
        _rootNodes.Clear();

        // Ordner zuerst einhängen, damit sie in jeder Ebene oben stehen.
        foreach (var folder in folders)
        {
            if (folder.Entry.ParentId is { } parentId && _foldersById.TryGetValue(parentId, out var parent))
                parent.Children.Add(folder);
            else
                _rootNodes.Add(folder);
        }

        foreach (var sound in _allSounds
                     .OrderBy(s => s.Entry.Order)
                     .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (sound.Entry.FolderId is { } folderId && _foldersById.TryGetValue(folderId, out var folder))
                folder.Children.Add(sound);
            else
                _rootNodes.Add(sound);
        }

        foreach (var folder in folders) folder.NotifyChildrenChanged();
    }

    /// <summary>Klappt den Baum in die sichtbare Liste auf – zugeklappte Ordner enden hier.</summary>
    private void Flatten(IEnumerable<object> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case FolderNodeViewModel folder:
                    folder.Depth = depth;
                    VisibleItems.Add(folder);
                    if (folder.IsExpanded) Flatten(folder.Children, depth + 1);
                    break;

                case SoundItemViewModel sound:
                    sound.Depth = depth;
                    VisibleItems.Add(sound);
                    break;
            }
        }
    }

    private int NextSoundOrder(Guid? folderId)
    {
        var siblings = _allSounds.Where(s => s.Entry.FolderId == folderId).ToList();
        return siblings.Count == 0 ? 0 : siblings.Max(s => s.Entry.Order) + 1;
    }

    private int NextFolderOrder(Guid? parentId)
    {
        var siblings = _allFolders.Where(f => f.Entry.ParentId == parentId).ToList();
        return siblings.Count == 0 ? 0 : siblings.Max(f => f.Entry.Order) + 1;
    }

    private string UniqueFolderName(Guid? parentId)
    {
        var taken = _allFolders
            .Where(f => f.Entry.ParentId == parentId)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        if (!taken.Contains("Neuer Ordner")) return "Neuer Ordner";

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"Neuer Ordner {i}";
            if (!taken.Contains(candidate)) return candidate;
        }

        return "Neuer Ordner";
    }

    // ================= Hintergrundarbeit =================

    /// <summary>
    /// Ermittelt Längen und wärmt den PCM-Cache vor, damit der erste Tastendruck
    /// nicht auf das Dekodieren warten muss. Läuft bewusst im Hintergrund und
    /// stoppt, sobald das Cache-Budget weitgehend belegt ist.
    /// </summary>
    public Task WarmUpAsync(IReadOnlyList<SoundItemViewModel>? subset = null)
    {
        var items = (subset ?? _allSounds).ToList();
        if (items.Count == 0) return Task.CompletedTask;

        return Task.Run(() =>
        {
            var cache = _services.Engine.Cache;

            foreach (var item in items)
            {
                var path = item.FilePath;

                if (!File.Exists(path))
                {
                    Dispatcher.UIThread.Post(() => item.IsMissing = true);
                    continue;
                }

                if (item.Entry.DurationSeconds <= 0 && AudioFormat.TryGetDuration(path) is { } duration)
                {
                    Dispatcher.UIThread.Post(() => item.SetDuration(duration));
                }

                if (cache.UsedBytes < cache.BudgetBytes * 0.8)
                {
                    cache.GetOrLoad(path);
                }
            }

            Dispatcher.UIThread.Post(PersistLibrary);
        });
    }
}
