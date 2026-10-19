using Soundbord.Audio;
using Soundbord.Models;

namespace Soundbord.Services;

public static class SoundLibrary
{
    public static (List<FolderEntry> Folders, List<SoundEntry> Sounds) Load()
    {
        var file = JsonStore.Load(AppPaths.LibraryFile, () => new SoundLibraryFile());

        var folders = file.Folders ?? [];
        var sounds = file.Sounds
            .Where(s => !string.IsNullOrWhiteSpace(s.FilePath))
            .ToList();

        // Verwaiste Zuordnungen aufräumen: zeigt ein Sound auf einen gelöschten
        // Ordner, landet er wieder oben statt unsichtbar zu werden.
        var known = folders.Select(f => f.Id).ToHashSet();
        foreach (var sound in sounds)
        {
            if (sound.FolderId is { } id && !known.Contains(id)) sound.FolderId = null;
        }

        foreach (var folder in folders)
        {
            if (folder.ParentId is { } parent && (!known.Contains(parent) || parent == folder.Id))
                folder.ParentId = null;
        }

        BreakCycles(folders);
        return (folders, sounds);
    }

    /// <summary>
    /// Schützt vor einem Ordner, der über Umwege sein eigener Vorfahre ist. Das kann
    /// nur durch eine von Hand bearbeitete Datei entstehen, würde den Aufbau des
    /// Baums aber in eine Endlosschleife schicken.
    /// </summary>
    private static void BreakCycles(List<FolderEntry> folders)
    {
        var byId = folders.ToDictionary(f => f.Id);

        foreach (var folder in folders)
        {
            var seen = new HashSet<Guid> { folder.Id };
            var current = folder;

            while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                if (!seen.Add(parent.Id))
                {
                    folder.ParentId = null;
                    break;
                }

                current = parent;
            }
        }
    }

    public static void Save(IEnumerable<FolderEntry> folders, IEnumerable<SoundEntry> sounds) =>
        JsonStore.Save(AppPaths.LibraryFile, new SoundLibraryFile
        {
            Folders = folders.ToList(),
            Sounds = sounds.ToList(),
        });

    /// <summary>
    /// Erweitert eine Auswahl aus Dateien und Ordnern zu einer Liste importierbarer
    /// Audiodateien. Ordner werden rekursiv durchsucht.
    /// </summary>
    public static List<string> ExpandPaths(IEnumerable<string> paths)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        if (AudioFormat.IsSupported(file) && seen.Add(Path.GetFullPath(file)))
                            files.Add(file);
                    }
                }
                else if (File.Exists(path) && AudioFormat.IsSupported(path) && seen.Add(Path.GetFullPath(path)))
                {
                    files.Add(path);
                }
            }
            catch (Exception)
            {
                // Unlesbare Ordner (Rechte, defekte Verknüpfungen) einfach überspringen.
            }
        }

        files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.CurrentCultureIgnoreCase));
        return files;
    }

    public static SoundEntry CreateEntry(string filePath) => new()
    {
        Name = Path.GetFileNameWithoutExtension(filePath),
        FilePath = Path.GetFullPath(filePath),
    };
}
