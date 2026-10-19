namespace Soundbord.Models;

/// <summary>Ein Eintrag der Sound-Bibliothek, so wie er in sounds.json landet.</summary>
public sealed class SoundEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public Hotkey? Hotkey { get; set; }

    public float Volume { get; set; } = 1f;

    public bool Loop { get; set; }

    /// <summary>Gecachte Länge, damit die Liste nicht beim Start jede Datei öffnen muss. 0 = unbekannt.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Ordner, in dem der Sound liegt. Null = oberste Ebene.</summary>
    public Guid? FolderId { get; set; }

    /// <summary>Reihenfolge innerhalb des Ordners.</summary>
    public int Order { get; set; }
}

/// <summary>Ein Ordner in der Bibliothek. Ordner dürfen ineinander liegen.</summary>
public sealed class FolderEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Neuer Ordner";

    public Guid? ParentId { get; set; }

    public bool IsExpanded { get; set; } = true;

    public int Order { get; set; }
}

public sealed class SoundLibraryFile
{
    public int Version { get; set; } = 2;

    public List<FolderEntry> Folders { get; set; } = [];

    public List<SoundEntry> Sounds { get; set; } = [];
}
