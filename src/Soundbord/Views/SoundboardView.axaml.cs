using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Soundbord.ViewModels;

namespace Soundbord.Views;

public partial class SoundboardView : UserControl
{
    /// <summary>Ab dieser Entfernung gilt eine Mausbewegung als Ziehen und nicht als Klick.</summary>
    private const double DragThreshold = 6;

    /// <summary>
    /// Das gezogene Element. Bewusst ein Feld statt einer Nutzlast im Datenobjekt:
    /// der Zug bleibt im Prozess, es muss nichts serialisiert werden, und beim
    /// Ablegen liegt genau dieselbe Instanz vor.
    /// </summary>
    private static object? _dragged;

    private object? _pressed;
    private Point _pressedAt;
    private FolderNodeViewModel? _highlighted;

    public SoundboardView()
    {
        InitializeComponent();

        AddHandler(PointerPressedEvent, OnPointerPressedPreview, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedPreview, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearHighlight());
    }

    private SoundboardViewModel? ViewModel => DataContext as SoundboardViewModel;

    // ================= Ziehen starten =================

    private void OnPointerPressedPreview(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;

        // Auf Reglern, Schaltern und Textfeldern darf kein Zug beginnen – sonst
        // ließe sich dort nichts mehr bedienen.
        if (IsInteractive(source)) return;

        _pressed = FindNode(source);
        _pressedAt = e.GetPosition(this);
    }

    private async void OnPointerMovedPreview(object? sender, PointerEventArgs e)
    {
        if (_pressed is null) return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = null;
            return;
        }

        var delta = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        var payload = _pressed;
        _pressed = null;
        _dragged = payload;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(DataFormat.Text, "soundbord-node"));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // Ein abgebrochener Zug ist kein Fehlerfall.
        }
        finally
        {
            _dragged = null;
            ClearHighlight();
        }
    }

    // ================= Ablegen =================

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Dateien von außen gehören dem Fenster – hier nicht abfangen, sonst
        // funktioniert der Import per Drag & Drop auf die Liste nicht mehr.
        if (_dragged is null) return;

        var target = ResolveDropTarget(e.Source as Visual);
        var allowed = IsAllowed(_dragged, target);

        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        Highlight(allowed ? target : null);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dragged is null) return;

        e.Handled = true;

        var payload = _dragged;
        var target = ResolveDropTarget(e.Source as Visual);
        ClearHighlight();

        if (!IsAllowed(payload, target) || ViewModel is not { } vm) return;

        switch (payload)
        {
            case SoundItemViewModel sound:
                vm.MoveSound(sound, target?.Id);
                break;

            case FolderNodeViewModel folder:
                vm.MoveFolder(folder, target?.Id);
                break;
        }
    }

    private bool IsAllowed(object payload, FolderNodeViewModel? target) => payload switch
    {
        SoundItemViewModel sound => sound.Entry.FolderId != target?.Id,
        FolderNodeViewModel folder => ViewModel?.CanMoveFolder(folder, target?.Id) ?? false,
        _ => false,
    };

    /// <summary>
    /// Ermittelt den Zielordner unter dem Zeiger. Über einem Sound gilt dessen
    /// Ordner als Ziel – so lässt sich neben einen Sound ablegen, ohne den Ordner
    /// selbst treffen zu müssen. Über leerer Fläche ist das Ziel die oberste Ebene.
    /// </summary>
    private FolderNodeViewModel? ResolveDropTarget(Visual? source) => FindNode(source) switch
    {
        FolderNodeViewModel folder => folder,
        SoundItemViewModel sound when sound.Entry.FolderId is { } id => FindFolder(id),
        _ => null,
    };

    private FolderNodeViewModel? FindFolder(Guid id) =>
        ViewModel?.VisibleItems.OfType<FolderNodeViewModel>().FirstOrDefault(f => f.Id == id);

    private static object? FindNode(Visual? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ListBoxItem item) return item.DataContext;
            current = current.GetVisualParent();
        }

        return null;
    }

    /// <summary>Bedienelemente, die den Zeiger für sich brauchen.</summary>
    private static bool IsInteractive(Visual source)
    {
        var current = source;
        while (current is not null and not ListBoxItem)
        {
            if (current is Slider or Button or ToggleButton or TextBox or ComboBox or Thumb) return true;
            current = current.GetVisualParent();
        }

        return false;
    }

    private void Highlight(FolderNodeViewModel? folder)
    {
        if (ReferenceEquals(_highlighted, folder)) return;

        if (_highlighted is not null) _highlighted.IsDropTarget = false;
        _highlighted = folder;
        if (_highlighted is not null) _highlighted.IsDropTarget = true;
    }

    private void ClearHighlight() => Highlight(null);
}
