using Avalonia.Threading;
using Soundbord.Audio;
using Soundbord.Models;

namespace Soundbord.Services;

/// <summary>
/// Kompositionswurzel. Bewusst manuell verdrahtet statt per DI-Container:
/// bei dieser Größe kostet ein Container nur Startzeit und Indirektion.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly DebouncedSaver _settingsSaver;
    private readonly DebouncedSaver _librarySaver;
    private bool _disposed;

    public AppServices()
    {
        Settings = JsonStore.Load(AppPaths.SettingsFile, () => new AppSettings());

        var (folders, sounds) = SoundLibrary.Load();
        Folders = folders;
        Sounds = sounds;

        Engine = new AudioEngine();
        Hotkeys = new GlobalHotkeyService();
        Triggers = new SerialWorkQueue("Soundbord.Trigger");
        Recorder = new HotkeyRecorder(Hotkeys);

        _settingsSaver = new DebouncedSaver(() => JsonStore.Save(AppPaths.SettingsFile, Settings));
        _librarySaver = new DebouncedSaver(() => SoundLibrary.Save(Folders, Sounds));

        // Aufnahme-Ergebnis aus dem Tastatur-Hook herausholen, bevor irgendetwas
        // passiert – im Hook selbst darf nicht gearbeitet werden.
        Hotkeys.CaptureCompleted += hotkey =>
            Dispatcher.UIThread.Post(() => Recorder.Complete(hotkey));
    }

    public AppSettings Settings { get; }

    public List<SoundEntry> Sounds { get; }

    public List<FolderEntry> Folders { get; }

    public AudioEngine Engine { get; }

    public GlobalHotkeyService Hotkeys { get; }

    /// <summary>Verarbeitet Tastendrücke außerhalb von Hook und UI-Thread.</summary>
    public SerialWorkQueue Triggers { get; }

    public HotkeyRecorder Recorder { get; }

    public void SaveSettingsSoon() => _settingsSaver.Request();

    public void SaveLibrarySoon() => _librarySaver.Request();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Hotkeys.Dispose();
        _settingsSaver.Dispose();  // schreibt ausstehende Änderungen noch weg
        _librarySaver.Dispose();
        Triggers.Dispose();
        Engine.Dispose();
    }
}

/// <summary>
/// Vermittelt das Aufnehmen einer Tastenkombination. Es kann immer nur eine
/// Aufnahme gleichzeitig laufen; eine neue bricht die vorherige sauber ab.
/// </summary>
public sealed class HotkeyRecorder(GlobalHotkeyService hotkeys)
{
    private Action<Hotkey?>? _pending;

    public bool IsRecording => _pending is not null;

    public void Begin(Action<Hotkey?> onResult)
    {
        Cancel();
        _pending = onResult;
        hotkeys.BeginCapture();
    }

    public void Cancel()
    {
        var pending = _pending;
        _pending = null;

        if (pending is null) return;

        hotkeys.CancelCapture();
        pending(null);
    }

    /// <summary>Wird aus dem UI-Thread aufgerufen, nachdem der Hook zurückgekehrt ist.</summary>
    internal void Complete(Hotkey? hotkey)
    {
        var pending = _pending;
        _pending = null;
        pending?.Invoke(hotkey);
    }
}
