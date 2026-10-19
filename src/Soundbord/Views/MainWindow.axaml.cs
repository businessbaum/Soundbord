using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Soundbord.ViewModels;

namespace Soundbord.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Opened += OnOpened;
        Closing += OnClosing;

        // Erst beim Aufklappen nachsehen, welche Programme gerade Ton machen.
        // Die Abfrage geht über COM durch alle Geräte und Sitzungen; sie im
        // Anzeigetakt zu wiederholen wäre Verschwendung.
        AudioAppPicker.DropDownOpened += (_, _) => ViewModel?.RefreshAudioApps();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnOpened(object? sender, EventArgs e) => ViewModel?.Initialize();

    private void OnClosing(object? sender, WindowClosingEventArgs e) => ViewModel?.Shutdown();

    // ================= Drag & Drop =================

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;

        if (hasFiles && ViewModel is { } vm)
        {
            vm.Soundboard.IsDragOver = true;
            vm.SelectedTab = 0;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm) vm.Soundboard.IsDragOver = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (ViewModel is not { } vm) return;
        vm.Soundboard.IsDragOver = false;

        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;

        var paths = items
            .Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count == 0) return;

        await vm.Soundboard.ImportAsync(paths);
    }
}
