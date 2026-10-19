using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundbord.Audio;
using Soundbord.Models;
using Soundbord.Services;

namespace Soundbord.ViewModels;

public enum StatusKind
{
    Good,
    Warning,
    Bad,
}

public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Takt der Wiedergabeanzeige. Fein genug für eine flüssige Leiste, grob genug um nichts zu kosten.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>Nur jeder n-te Takt prüft das Windows-Standardmikrofon – das ist ein COM-Aufruf.</summary>
    private const int DefaultMicCheckEveryTicks = 25;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;

    private bool _suppressDefaultMicWrite;
    private bool _suppressAudioAppWrite;
    private int _tick;

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _statusText = "Startet…";
    [ObservableProperty] private StatusKind _statusKind = StatusKind.Warning;
    [ObservableProperty] private string _statusDetail = string.Empty;

    // --- Wiedergabeleiste ---
    [ObservableProperty] private bool _hasPlayback;
    [ObservableProperty] private string _nowPlayingName = string.Empty;
    [ObservableProperty] private double _nowPlayingProgress;
    [ObservableProperty] private string _nowPlayingTime = string.Empty;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _nowPlayingLooping;
    [ObservableProperty] private string _extraPlaybackText = string.Empty;
    [ObservableProperty] private bool _hasExtraPlayback;

    // --- Windows-Standardmikrofon ---
    [ObservableProperty] private AudioDevice? _selectedDefaultMic;
    [ObservableProperty] private string? _defaultMicMessage;

    // --- Desktop-Ton ---
    [ObservableProperty] private bool _desktopTunnelActive;
    [ObservableProperty] private string? _desktopTunnelNote;
    [ObservableProperty] private AudioApp? _selectedAudioApp;

    private Guid _currentSoundId;

    public MainWindowViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;

        Soundboard = new SoundboardViewModel(services, dialogs, RefreshHotkeyBindings);
        Settings = new SettingsViewModel(services, dialogs, RefreshHotkeyBindings, UpdateStatus);

        _services.Engine.SoundStateChanged += OnSoundStateChanged;
        _services.Engine.RoutesChanged += () => Dispatcher.UIThread.Post(UpdateStatus);
        _services.Engine.DesktopTunnelChanged += () => Dispatcher.UIThread.Post(RefreshDesktopTunnel);

        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, OnTick);
    }

    public SoundboardViewModel Soundboard { get; }

    public SettingsViewModel Settings { get; }

    public bool IsStatusGood => StatusKind == StatusKind.Good;
    public bool IsStatusWarning => StatusKind == StatusKind.Warning;
    public bool IsStatusBad => StatusKind == StatusKind.Bad;

    partial void OnStatusKindChanged(StatusKind value)
    {
        OnPropertyChanged(nameof(IsStatusGood));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusBad));
    }

    // ================= Pegel =================

    /// <summary>
    /// Pegel der Sounds. Wirkt ausschließlich auf die Sounds – das Mikrofon hängt im
    /// Graphen hinter diesem Regler und bleibt davon unberührt.
    /// </summary>
    public double MasterVolume
    {
        get => _services.Settings.MasterVolume;
        set
        {
            var clamped = (float)Math.Clamp(value, 0d, 1d);
            if (Math.Abs(clamped - _services.Settings.MasterVolume) < 0.001f) return;

            _services.Settings.MasterVolume = clamped;
            ApplyLevels();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MasterVolumeText));
            OnPropertyChanged(nameof(MasterVolumeTip));
        }
    }

    public string MasterVolumeText => $"{_services.Settings.MasterVolume * 100:0}%";

    public string MasterVolumeTip =>
        $"Lautstärke der Sounds · {VolumeCurve.Describe(_services.Settings.MasterVolume)}\n" +
        "Wirkt nicht auf das Mikrofon.";

    /// <summary>Pegel des Mikrofons. Unabhängig von <see cref="MasterVolume"/>.</summary>
    public double MicVolume
    {
        get => _services.Settings.MicVolume;
        set
        {
            var clamped = (float)Math.Clamp(value, 0d, 2d);
            if (Math.Abs(clamped - _services.Settings.MicVolume) < 0.001f) return;

            _services.Settings.MicVolume = clamped;
            ApplyLevels();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MicVolumeText));
            OnPropertyChanged(nameof(MicVolumeTip));
        }
    }

    public string MicVolumeText => $"{_services.Settings.MicVolume * 100:0}%";

    public string MicVolumeTip =>
        $"Lautstärke des Mikrofons · {VolumeCurve.Describe(_services.Settings.MicVolume)}\n" +
        "Wirkt nicht auf die Sounds.";

    public bool MicSliderEnabled => _services.Settings.MicPassthroughEnabled;

    /// <summary>Pegel des Desktop-Tons. Hängt an keinem der anderen Regler.</summary>
    public double DesktopVolume
    {
        get => _services.Settings.DesktopVolume;
        set
        {
            var clamped = (float)Math.Clamp(value, 0d, 2d);
            if (Math.Abs(clamped - _services.Settings.DesktopVolume) < 0.001f) return;

            _services.Settings.DesktopVolume = clamped;
            ApplyLevels();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DesktopVolumeText));
            OnPropertyChanged(nameof(DesktopVolumeTip));
        }
    }

    public string DesktopVolumeText => $"{_services.Settings.DesktopVolume * 100:0}%";

    public string DesktopVolumeTip =>
        $"Lautstärke des Desktop-Tons im Kabel · {VolumeCurve.Describe(_services.Settings.DesktopVolume)}\n" +
        "Ändert nichts an dem, was du selbst hörst.";

    private void ApplyLevels()
    {
        _services.Engine.Apply(_services.Settings);
        _services.SaveSettingsSoon();
        Settings.NotifyLevelsChanged();
    }

    // ================= Windows-Standardmikrofon =================

    public ObservableCollection<AudioDevice> DefaultMicDevices { get; } = [];

    /// <summary>Ohne die undokumentierte Schnittstelle bleibt die Auswahl ausgeblendet.</summary>
    public bool DefaultMicSupported { get; } = DefaultDeviceSwitcher.IsSupported;

    partial void OnSelectedDefaultMicChanged(AudioDevice? value)
    {
        if (_suppressDefaultMicWrite || value is null) return;

        if (DefaultDeviceSwitcher.TrySetDefaultCaptureDevice(value.Id, out var error))
        {
            DefaultMicMessage = null;
            // Unsere eigenen "Systemstandard"-Zuordnungen zeigen jetzt woandershin.
            _services.Engine.Apply(_services.Settings);
            UpdateStatus();
        }
        else
        {
            DefaultMicMessage = error;
        }
    }

    private void RefreshDefaultMic()
    {
        var devices = AudioDevices.Capture();
        var currentId = DefaultDeviceSwitcher.GetDefaultCaptureDeviceId();

        var sameList = devices.Count == DefaultMicDevices.Count &&
                       devices.Select(d => d.Id).SequenceEqual(DefaultMicDevices.Select(d => d.Id));

        _suppressDefaultMicWrite = true;
        try
        {
            if (!sameList)
            {
                DefaultMicDevices.Clear();
                foreach (var device in devices) DefaultMicDevices.Add(device);
            }

            var match = DefaultMicDevices.FirstOrDefault(d => d.Id == currentId);
            if (!ReferenceEquals(match, SelectedDefaultMic)) SelectedDefaultMic = match;
        }
        finally
        {
            _suppressDefaultMicWrite = false;
        }
    }

    // ================= Desktop-Ton =================

    /// <summary>
    /// Schleift den laufenden Systemton in das virtuelle Kabel. Gedacht als kurzer
    /// Tunnel: Musik, Videoton oder Spielsound für die anderen hörbar machen und
    /// gleich wieder zu.
    /// </summary>
    [RelayCommand]
    private void ToggleDesktopTunnel() =>
        _services.Triggers.Post(() => _services.Engine.ToggleDesktopTunnel());

    private void RefreshDesktopTunnel()
    {
        var engine = _services.Engine;

        DesktopTunnelActive = engine.DesktopTunnelActive;
        DesktopTunnelNote = engine.DesktopTunnelNote;

        OnPropertyChanged(nameof(DesktopTunnelLabel));
        OnPropertyChanged(nameof(DesktopTunnelTip));
    }

    // --- Auswahl der Anwendung ---

    public ObservableCollection<AudioApp> AudioApps { get; } = [];

    /// <summary>
    /// Liest die laufenden Anwendungen neu ein. Wird beim Aufklappen der Liste aufgerufen
    /// statt im Anzeigetakt: die Abfrage geht über alle Prozesse und alle Audiositzungen,
    /// und außerhalb dieses einen Moments interessiert das Ergebnis nicht.
    /// </summary>
    public void RefreshAudioApps()
    {
        var stored = _services.Settings.DesktopTunnelApp;
        var apps = RunningApps.List();

        var list = new List<AudioApp>(apps.Count + 2) { AudioApp.WholeDesktop };
        list.AddRange(apps);

        // Die gemerkte Anwendung bleibt wählbar, auch wenn sie gerade nicht läuft –
        // sonst würde die Auswahl beim Aufklappen stillschweigend zurückspringen.
        if (!string.IsNullOrWhiteSpace(stored) &&
            !list.Any(a => string.Equals(a.Key, stored, StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(1, new AudioApp(stored, $"{stored} (läuft nicht)", 0, false));
        }

        var unchanged = list.Count == AudioApps.Count &&
                        list.Select(a => a.Key).SequenceEqual(AudioApps.Select(a => a.Key));

        _suppressAudioAppWrite = true;
        try
        {
            if (!unchanged)
            {
                AudioApps.Clear();
                foreach (var app in list) AudioApps.Add(app);
            }

            var match = AudioApps.FirstOrDefault(
                            a => string.Equals(a.Key, stored ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        ?? AudioApps.FirstOrDefault();

            if (!ReferenceEquals(match, SelectedAudioApp)) SelectedAudioApp = match;
        }
        finally
        {
            _suppressAudioAppWrite = false;
        }
    }

    partial void OnSelectedAudioAppChanged(AudioApp? value)
    {
        if (_suppressAudioAppWrite || value is null) return;

        var key = value.IsWholeDesktop ? null : value.Key;
        if (key == _services.Settings.DesktopTunnelApp) return;

        _services.Settings.DesktopTunnelApp = key;

        // Läuft der Tunnel, hängt die Engine ihn auf die neue Anwendung um.
        _services.Engine.Apply(_services.Settings);
        _services.SaveSettingsSoon();

        OnPropertyChanged(nameof(DesktopTunnelLabel));
        OnPropertyChanged(nameof(DesktopTunnelTip));
    }

    public string DesktopTunnelLabel => DesktopTunnelActive ? "Ton an" : "Ton übertragen";

    public string DesktopTunnelTip
    {
        get
        {
            var hotkey = _services.Settings.DesktopTunnelHotkey is { IsEmpty: false } key
                ? $"\nHotkey: {key.DisplayName}"
                : "\nEin Hotkey dafür lässt sich in den Einstellungen vergeben.";

            if (DesktopTunnelActive)
                return (DesktopTunnelNote ?? "Der Ton läuft ins Kabel.") + "\n\nZum Beenden klicken." + hotkey;

            var reason = DesktopTunnelNote is null ? string.Empty : $"\n\n{DesktopTunnelNote}";

            var what = SelectedAudioApp is { IsWholeDesktop: false } app
                ? $"den Ton von „{app.Name}“"
                : "den gesamten Systemton – Musik, Videos, Spielton";

            return $"Schleift {what} in das virtuelle Kabel, sodass die anderen ihn wie über " +
                   "dein Mikrofon hören. Rechts wählst du aus, welche Anwendung." + reason + hotkey;
        }
    }

    partial void OnDesktopTunnelActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(DesktopTunnelLabel));
        OnPropertyChanged(nameof(DesktopTunnelTip));
    }

    // ================= Wiedergabeleiste =================

    private void OnTick(object? sender, EventArgs e)
    {
        var status = _services.Engine.GetStatus();

        if (status is null)
        {
            if (HasPlayback)
            {
                HasPlayback = false;
                HasExtraPlayback = false;
                NowPlayingProgress = 0;
                _currentSoundId = Guid.Empty;
            }
        }
        else
        {
            _currentSoundId = status.SoundId;
            HasPlayback = true;
            NowPlayingName = status.Name;
            IsPaused = status.IsPaused;
            NowPlayingLooping = status.IsLooping;

            NowPlayingProgress = status.Duration > TimeSpan.Zero
                ? Math.Clamp(status.Position.TotalSeconds / status.Duration.TotalSeconds, 0, 1)
                : 0;

            NowPlayingTime = status.Duration > TimeSpan.Zero
                ? $"{Format(status.Position)} / {Format(status.Duration)}"
                : Format(status.Position);

            HasExtraPlayback = status.ActiveCount > 1;
            ExtraPlaybackText = status.ActiveCount > 1 ? $"+{status.ActiveCount - 1}" : string.Empty;
        }

        // Der Übersteuerungsschutz meldet sich nur, wenn er wirklich eingreift.
        if (HasPlayback) Settings.PollLimiter();

        if (++_tick >= DefaultMicCheckEveryTicks)
        {
            _tick = 0;
            if (DefaultMicSupported) RefreshDefaultMic();
        }
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    [RelayCommand]
    private void TogglePause()
    {
        if (_currentSoundId == Guid.Empty) return;

        var id = _currentSoundId;
        _services.Triggers.Post(() => _services.Engine.TogglePause(id));
    }

    [RelayCommand]
    private void StopCurrent()
    {
        if (_currentSoundId == Guid.Empty) return;

        var id = _currentSoundId;
        _services.Triggers.Post(() => _services.Engine.Stop(id));
    }

    // ================= Start und Zustand =================

    /// <summary>Startet Audio und Hotkeys. Wird aufgerufen, sobald das Fenster steht.</summary>
    public void Initialize()
    {
        var settings = _services.Settings;

        _services.Engine.Apply(settings);

        _services.Hotkeys.Enabled = settings.HotkeysEnabled;
        _services.Hotkeys.SuppressMatched = settings.SuppressHotkeysInOtherApps;

        // Muss aus dem UI-Thread passieren: nur der hat eine Nachrichtenschleife,
        // ohne die Windows den Tastatur-Hook niemals aufruft.
        var installed = _services.Hotkeys.Install();

        RefreshHotkeyBindings();
        if (DefaultMicSupported) RefreshDefaultMic();
        RefreshAudioApps();
        RefreshDesktopTunnel();
        UpdateStatus();

        if (!installed) StatusDetail = "Hotkeys konnten nicht registriert werden.";

        _timer.Start();
        _ = Soundboard.WarmUpAsync();
    }

    [RelayCommand]
    private void StopAll() => _services.Triggers.Post(() => _services.Engine.StopAll());

    public void RefreshHotkeyBindings()
    {
        var bindings = new List<(Hotkey, Action)>();

        foreach (var item in Soundboard.All)
        {
            if (item.Entry.Hotkey is not { IsEmpty: false } hotkey) continue;

            var entry = item.Entry;
            bindings.Add((hotkey, () => _services.Triggers.Post(() => _services.Engine.Toggle(entry))));
        }

        if (_services.Settings.StopAllHotkey is { IsEmpty: false } stopAll)
        {
            bindings.Add((stopAll, () => _services.Triggers.Post(() => _services.Engine.StopAll())));
        }

        if (_services.Settings.DesktopTunnelHotkey is { IsEmpty: false } desktop)
        {
            bindings.Add((desktop, () => _services.Triggers.Post(() => _services.Engine.ToggleDesktopTunnel())));
        }

        OnPropertyChanged(nameof(DesktopTunnelTip));

        _services.Hotkeys.SetBindings(bindings);
    }

    private void OnSoundStateChanged(Guid soundId, bool playing) =>
        Dispatcher.UIThread.Post(() => Soundboard.SetPlayingState(soundId, playing));

    /// <summary>Fasst den Zustand der Audio-Kette in einer Ampel zusammen.</summary>
    public void UpdateStatus()
    {
        var engine = _services.Engine;
        var settings = _services.Settings;

        if (!settings.VirtualOutputEnabled)
        {
            StatusKind = StatusKind.Warning;
            StatusText = "Nur Mithören";
            StatusDetail = "Der virtuelle Ausgang ist deaktiviert – andere Programme hören nichts.";
        }
        else if (!engine.VirtualRouteActive)
        {
            StatusKind = StatusKind.Bad;
            StatusText = "Kein Ausgang";
            StatusDetail = engine.LastError ?? "Das Wiedergabegerät konnte nicht geöffnet werden.";
        }
        else if (engine.VirtualRouteDeviceName is { } name && !VirtualCableDetector.IsVirtualCableDevice(name))
        {
            StatusKind = StatusKind.Warning;
            StatusText = "Kein virtuelles Kabel";
            StatusDetail = $"„{name}“ ist kein virtuelles Kabel. Andere Programme können den Ton so nicht " +
                           "als Mikrofon empfangen; das Mikrofon wird deshalb nicht durchgeschleift.";
        }
        else
        {
            StatusKind = StatusKind.Good;
            StatusText = engine.MicActive ? "Aktiv · Mikrofon gemischt" : "Aktiv";
            StatusDetail = engine.LastError ?? $"Ausgabe an {engine.VirtualRouteDeviceName}";
        }

        OnPropertyChanged(nameof(MasterVolume));
        OnPropertyChanged(nameof(MasterVolumeText));
        OnPropertyChanged(nameof(MasterVolumeTip));
        OnPropertyChanged(nameof(MicVolume));
        OnPropertyChanged(nameof(MicVolumeText));
        OnPropertyChanged(nameof(MicVolumeTip));
        OnPropertyChanged(nameof(MicSliderEnabled));
        OnPropertyChanged(nameof(DesktopVolume));
        OnPropertyChanged(nameof(DesktopVolumeText));
        OnPropertyChanged(nameof(DesktopVolumeTip));

        // Die Einstellungsseite zeigt Teile des Engine-Zustands mit an.
        Settings.NotifyEngineStateChanged();
    }

    public void Shutdown()
    {
        _timer.Stop();
        _services.Engine.SoundStateChanged -= OnSoundStateChanged;
        _services.Recorder.Cancel();
        _services.Hotkeys.Uninstall();
    }
}
