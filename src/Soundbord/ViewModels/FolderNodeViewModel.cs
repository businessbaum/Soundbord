using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundbord.Models;

namespace Soundbord.ViewModels;

/// <summary>Ein Ordner im Baum. Enthält Unterordner und Sounds.</summary>
public partial class FolderNodeViewModel : ObservableObject
{
    private readonly SoundboardViewModel _owner;

    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private int _depth;

    /// <summary>Einrückung nach Verschachtelungstiefe – ersetzt die Struktur eines TreeViews.</summary>
    public Avalonia.Thickness Indent => new(Depth * 22, 0, 0, 0);

    partial void OnDepthChanged(int value) => OnPropertyChanged(nameof(Indent));

    public FolderNodeViewModel(FolderEntry entry, SoundboardViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public FolderEntry Entry { get; }

    public Guid Id => Entry.Id;

    /// <summary>Unterordner zuerst, danach die Sounds.</summary>
    public ObservableCollection<object> Children { get; } = [];

    public string Name
    {
        get => Entry.Name;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed == Entry.Name) return;
            Entry.Name = trimmed;
            OnPropertyChanged();
            _owner.PersistLibrary();
        }
    }

    public bool IsExpanded
    {
        get => Entry.IsExpanded;
        set
        {
            if (value == Entry.IsExpanded) return;
            Entry.IsExpanded = value;
            OnPropertyChanged();
            _owner.ApplyView();
            _owner.PersistLibrary();
        }
    }

    /// <summary>Anzahl der Sounds einschließlich aller Unterordner.</summary>
    public int TotalSoundCount
    {
        get
        {
            var count = 0;
            foreach (var child in Children)
            {
                if (child is SoundItemViewModel) count++;
                else if (child is FolderNodeViewModel folder) count += folder.TotalSoundCount;
            }

            return count;
        }
    }

    public string CountText => TotalSoundCount == 1 ? "1 Sound" : $"{TotalSoundCount} Sounds";

    public bool HasChildren => Children.Count > 0;

    public void NotifyChildrenChanged()
    {
        OnPropertyChanged(nameof(TotalSoundCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasChildren));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void AddSubfolder() => _owner.AddFolder(this);

    [RelayCommand]
    private void Remove() => _owner.RemoveFolder(this);
}
