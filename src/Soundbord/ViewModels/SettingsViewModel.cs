using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundbord.Audio;
using Soundbord.Models;
using Soundbord.Services;

namespace Soundbord.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const string CableDownloadUrl = "https://vb-audio.com/Cable/";

    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private readonly Action _bindingsChanged;
    private readonly Action _statusChanged;

    private bool _loading;

    [ObservableProperty] private AudioDevice? _selectedVirtualDevice;
    [ObservableProperty] private AudioDevice? _selectedMonitorDevice;
    [ObservableProperty] private AudioDevice? _selectedMicDevice;
    [ObservableProperty] private bool _isRecordingStopAll;
    [ObservableProperty] private bool _isRecordingDesktopTunnel;
    [ObservableProperty] private string? _setupMessage;
    [ObservableProperty] private bool _setupMessageIsGood;

    public SettingsViewModel(AppServices services, IDialogService dialogs, Action bindingsChanged, Action statusChanged)
    {
        _services = services;
        _dialogs = dialogs;
        _bindingsChanged = bindingsChanged;
        _statusChanged = statusChanged;

        RefreshDevices();
    }

    private AppSettings Settings => _services.Settings;

    public ObservableCollection<AudioDevice> RenderDevices { get; } = [];

    public ObservableCollection<AudioDevice> CaptureDevices { get; } = [];

    // ================= Routing =================

    public bool VirtualOutputEnabled
    {
        get => Settings.VirtualOutputEnabled;
        set => Set(v => Settings.VirtualOutputEnabled = v, value, Settings.VirtualOutputEnabled);
    }

    public bool MonitorEnabled
    {
        get => Settings.MonitorEnabled;
        set => Set(v => Settings.MonitorEnabled = v, value, Settings.MonitorEnabled);
    }

    public bool MicPassthroughEnabled
    {
        get => Settings.MicPassthroughEnabled;
        set => Set(v => Settings.MicPassthroughEnabled = v, value, Settings.MicPassthroughEnabled);
    }

    /// <summary>Aus = du hörst dich selbst nicht. So ist es voreingestellt.</summary>
    public bool MicMonitorEnabled
    {
        get => Settings.MicMonitorEnabled;
        set => Set(v => Settings.MicMonitorEnabled = v, value, Settings.MicMonitorEnabled, nameof(MicMonitorHint));
    }

    public string MicMonitorHint => Settings.MicMonitorEnabled
        ? "Achtung: Du hörst dich mit rund 60 ms Verzögerung selbst. Das erschwert das Sprechen spürbar."
        : "Aus: Die anderen hören dich, du dich selbst nicht.";

    /// <summary>
    /// Sichtbar, wenn das Durchschleifen zwar eingeschaltet ist, mangels virtuellem
    /// Kabel aber nichts tut. Ohne diesen Hinweis wäre der Schalter eine Lüge.
    /// </summary>
    public bool ShowMicRoutingWarning =>
        Settings.MicPassthroughEnabled && !_services.Engine.MicReachesVirtualOutput;

    public string MicRoutingWarning =>
        "Derzeit wirkungslos: Der virtuelle Ausgang zeigt auf ein echtes Wiedergabegerät. " +
        "Das Mikrofon wird bewusst nicht dorthin geleitet – sonst hörst du dich über die " +
        "eigenen Kopfhörer, bei Lautsprechern gäbe es eine Rückkopplung. " +
        "Richte oben ein virtuelles Kabel ein.";

    /// <summary>Wird nach jedem Neuaufbau der Audio-Kette aufgerufen.</summary>
    public void NotifyEngineStateChanged()
    {
        OnPropertyChanged(nameof(ShowMicRoutingWarning));
        OnPropertyChanged(nameof(MicRoutingWarning));
        OnPropertyChanged(nameof(FormatText));
    }

    /// <summary>Wird im Anzeigetakt aufgerufen, solange die Einstellungen sichtbar sind.</summary>
    public void PollLimiter()
    {
        if (_services.Engine.ConsumeLimiterEngaged()) LimiterEngaged = true;
    }

    // ================= Pegel =================

    public double MasterVolume
    {
        get => Settings.MasterVolume;
        set => Set(v => Settings.MasterVolume = (float)v, value, Settings.MasterVolume, nameof(MasterVolumeText));
    }

    public double MonitorVolume
    {
        get => Settings.MonitorVolume;
        set => Set(v => Settings.MonitorVolume = (float)v, value, Settings.MonitorVolume, nameof(MonitorVolumeText));
    }

    public double MicVolume
    {
        get => Settings.MicVolume;
        set => Set(v => Settings.MicVolume = (float)v, value, Settings.MicVolume, nameof(MicVolumeText));
    }

    /// <summary>Die Regler in der Kopfzeile schreiben dieselben Einstellungen – hier nachziehen.</summary>
    public void NotifyLevelsChanged()
    {
        OnPropertyChanged(nameof(MasterVolume));
        OnPropertyChanged(nameof(MasterVolumeText));
        OnPropertyChanged(nameof(MicVolume));
        OnPropertyChanged(nameof(MicVolumeText));
    }

    public string MasterVolumeText => $"{Settings.MasterVolume * 100:0}%";
    public string MonitorVolumeText => $"{Settings.MonitorVolume * 100:0}%";
    public string MicVolumeText => $"{Settings.MicVolume * 100:0}%";

    // ================= Audio =================

    public double BufferMilliseconds
    {
        get => Settings.BufferMilliseconds;
        set
        {
            var rounded = (int)Math.Round(value);
            if (rounded == Settings.BufferMilliseconds) return;

            Settings.BufferMilliseconds = rounded;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BufferText));
            OnPropertyChanged(nameof(MicLatencyText));

            if (_loading) return;
            ApplyToEngine();
        }
    }

    public string BufferText => $"{Settings.BufferMilliseconds} ms";

    /// <summary>
    /// Was beim Sprechen tatsächlich an Verzögerung anfällt: Aufnahmepuffer,
    /// zulässiger Rückstand und Ausgabepuffer summiert.
    /// </summary>
    public string MicLatencyText =>
        $"Mikrofon-Verzögerung: ca. {AudioEngine.EstimateMicLatencyMs(Settings.BufferMilliseconds)} ms";

    public bool LimiterEnabled
    {
        get => Settings.LimiterEnabled;
        set => Set(v => Settings.LimiterEnabled = v, value, Settings.LimiterEnabled);
    }

    [ObservableProperty] private bool _limiterEngaged;

    /// <summary>
    /// Gegenüberstellung von interner Verarbeitung und Gerät. Stimmen beide überein,
    /// wird auf dem Weg zum Kabel nichts umgerechnet und nichts verworfen.
    /// </summary>
    public string FormatText
    {
        get
        {
            var device = _services.Engine.VirtualRouteFormatText;
            if (device is null) return $"Intern: {AudioEngine.InternalFormatText}";

            var internalText = AudioEngine.InternalFormatText;
            var matches = device.StartsWith($"{AudioFormat.SampleRate} Hz", StringComparison.Ordinal);

            return $"Intern: {internalText}\nKabel:  {device}\n" +
                   (matches
                       ? "Beides identisch – auf dem Weg zum Kabel wird nichts umgerechnet."
                       : "Abweichende Abtastrate – Windows rechnet um. In den Windows-Sound-Einstellungen " +
                         $"lässt sich das Kabel auf {AudioFormat.SampleRate} Hz stellen.");
        }
    }

    public double CacheBudgetMegabytes
    {
        get => Settings.CacheBudgetMegabytes;
        set => Set(v => Settings.CacheBudgetMegabytes = (int)Math.Round(v), value, Settings.CacheBudgetMegabytes, nameof(CacheText));
    }

    public string CacheText => $"{Settings.CacheBudgetMegabytes} MB";

    public string CacheUsageText
    {
        get
        {
            var cache = _services.Engine.Cache;
            var label = cache.Count == 1 ? "1 Sound" : $"{cache.Count} Sounds";
            return $"{label} im RAM · {cache.UsedBytes / (1024.0 * 1024.0):0.0} MB";
        }
    }

    // ================= Hotkeys =================

    public bool HotkeysEnabled
    {
        get => Settings.HotkeysEnabled;
        set
        {
            if (value == Settings.HotkeysEnabled) return;
            Settings.HotkeysEnabled = value;
            _services.Hotkeys.Enabled = value;
            OnPropertyChanged();
            _services.SaveSettingsSoon();
        }
    }

    public bool SuppressHotkeysInOtherApps
    {
        get => Settings.SuppressHotkeysInOtherApps;
        set
        {
            if (value == Settings.SuppressHotkeysInOtherApps) return;
            Settings.SuppressHotkeysInOtherApps = value;
            _services.Hotkeys.SuppressMatched = value;
            OnPropertyChanged();
            _services.SaveSettingsSoon();
        }
    }

    public bool StopOthersOnPlay
    {
        get => Settings.StopOthersOnPlay;
        set => Set(v => Settings.StopOthersOnPlay = v, value, Settings.StopOthersOnPlay);
    }

    public string StopAllHotkeyText => IsRecordingStopAll
        ? "Taste drücken…"
        : Settings.StopAllHotkey is { IsEmpty: false } hotkey ? hotkey.DisplayName : "Hotkey…";

    [RelayCommand]
    private void RecordStopAllHotkey()
    {
        if (IsRecordingStopAll)
        {
            _services.Recorder.Cancel();
            return;
        }

        IsRecordingStopAll = true;

        _services.Recorder.Begin(hotkey =>
        {
            IsRecordingStopAll = false;
            if (hotkey is null) return;

            Settings.StopAllHotkey = hotkey;
            OnPropertyChanged(nameof(StopAllHotkeyText));
            _services.SaveSettingsSoon();
            _bindingsChanged();
        });
    }

    [RelayCommand]
    private void ClearStopAllHotkey()
    {
        Settings.StopAllHotkey = null;
        OnPropertyChanged(nameof(StopAllHotkeyText));
        _services.SaveSettingsSoon();
        _bindingsChanged();
    }

    partial void OnIsRecordingStopAllChanged(bool value) => OnPropertyChanged(nameof(StopAllHotkeyText));

    public string DesktopTunnelHotkeyText => IsRecordingDesktopTunnel
        ? "Taste drücken…"
        : Settings.DesktopTunnelHotkey is { IsEmpty: false } hotkey ? hotkey.DisplayName : "Hotkey…";

    [RelayCommand]
    private void RecordDesktopTunnelHotkey()
    {
        if (IsRecordingDesktopTunnel)
        {
            _services.Recorder.Cancel();
            return;
        }

        IsRecordingDesktopTunnel = true;

        _services.Recorder.Begin(hotkey =>
        {
            IsRecordingDesktopTunnel = false;
            if (hotkey is null) return;

            Settings.DesktopTunnelHotkey = hotkey;
            OnPropertyChanged(nameof(DesktopTunnelHotkeyText));
            _services.SaveSettingsSoon();
            _bindingsChanged();
        });
    }

    [RelayCommand]
    private void ClearDesktopTunnelHotkey()
    {
        Settings.DesktopTunnelHotkey = null;
        OnPropertyChanged(nameof(DesktopTunnelHotkeyText));
        _services.SaveSettingsSoon();
        _bindingsChanged();
    }

    partial void OnIsRecordingDesktopTunnelChanged(bool value) =>
        OnPropertyChanged(nameof(DesktopTunnelHotkeyText));

    // ================= Virtuelles Kabel =================

    public bool CableDetected { get; private set; }

    public string CableTitle { get; private set; } = "Virtuelles Kabel";

    public string CableStatusText { get; private set; } = string.Empty;

    public string CableInstruction { get; private set; } = string.Empty;

    public bool ShowCableInstruction => CableDetected;

    private VirtualCableInfo? _cable;

    [RelayCommand]
    private void RefreshDevices()
    {
        _loading = true;
        try
        {
            var render = AudioDevices.Render();
            var capture = AudioDevices.Capture();

            RenderDevices.Clear();
            RenderDevices.Add(AudioDevice.SystemDefaultRender);
            foreach (var device in render) RenderDevices.Add(device);

            CaptureDevices.Clear();
            CaptureDevices.Add(AudioDevice.SystemDefaultCapture);
            foreach (var device in capture) CaptureDevices.Add(device);

            SelectedVirtualDevice = Match(RenderDevices, Settings.VirtualOutputDeviceId);
            SelectedMonitorDevice = Match(RenderDevices, Settings.MonitorOutputDeviceId);
            SelectedMicDevice = Match(CaptureDevices, Settings.MicDeviceId);

            _cable = VirtualCableDetector.Detect(render, capture);
            UpdateCableStatus();
        }
        finally
        {
            _loading = false;
        }

        RaiseAll();
    }

    private void UpdateCableStatus()
    {
        CableDetected = _cable is not null;

        if (_cable is null)
        {
            CableTitle = "Kein virtuelles Kabel gefunden";
            CableStatusText =
                "Ohne virtuelles Audiokabel kann Windows den Ton nicht als Mikrofon an andere Programme weitergeben. " +
                "Installiere VB-CABLE (kostenlos) und klicke danach auf „Erneut suchen“.";
            CableInstruction = string.Empty;
        }
        else
        {
            CableTitle = $"{_cable.ProductName} erkannt";
            CableStatusText = $"Wiedergabe an: {_cable.RenderDevice.Name}";
            CableInstruction =
                $"Stelle in Discord, TeamSpeak oder OBS das Eingabegerät auf „{_cable.CaptureDeviceName}“.";
        }

        OnPropertyChanged(nameof(CableDetected));
        OnPropertyChanged(nameof(CableTitle));
        OnPropertyChanged(nameof(CableStatusText));
        OnPropertyChanged(nameof(CableInstruction));
        OnPropertyChanged(nameof(ShowCableInstruction));
    }

    /// <summary>
    /// Setzt in einem Schritt alles so, dass der Ton im Voice-Chat ankommt und das
    /// eigene Mikrofon weiter funktioniert.
    /// </summary>
    [RelayCommand]
    private void AutoSetup()
    {
        RefreshDevices();

        if (_cable is null)
        {
            SetupMessageIsGood = false;
            SetupMessage = "Es ist kein virtuelles Audiokabel installiert. Installiere zuerst VB-CABLE.";
            return;
        }

        _loading = true;
        try
        {
            Settings.VirtualOutputEnabled = true;
            Settings.VirtualOutputDeviceId = _cable.RenderDevice.Id;

            // Mithören: alles außer dem Kabel selbst, sonst hörst du nichts.
            if (Settings.MonitorOutputDeviceId is null ||
                RenderDevices.FirstOrDefault(d => d.Id == Settings.MonitorOutputDeviceId) is { } monitor &&
                VirtualCableDetector.IsVirtualCableDevice(monitor.Name))
            {
                Settings.MonitorOutputDeviceId = AudioDevice.DefaultId;
            }

            Settings.MonitorEnabled = true;

            // Mikrofon durchschleifen, damit man neben den Sounds noch sprechen kann.
            var mic = CaptureDevices.FirstOrDefault(d => d.Id == Settings.MicDeviceId);
            if (mic is null || VirtualCableDetector.IsVirtualCableDevice(mic.Name))
            {
                Settings.MicDeviceId = AudioDevice.DefaultId;
            }

            Settings.MicPassthroughEnabled = true;

            SelectedVirtualDevice = Match(RenderDevices, Settings.VirtualOutputDeviceId);
            SelectedMonitorDevice = Match(RenderDevices, Settings.MonitorOutputDeviceId);
            SelectedMicDevice = Match(CaptureDevices, Settings.MicDeviceId);
        }
        finally
        {
            _loading = false;
        }

        ApplyToEngine();
        RaiseAll();

        SetupMessageIsGood = true;
        SetupMessage = $"Eingerichtet. Stelle jetzt in Discord/TeamSpeak das Eingabegerät auf „{_cable.CaptureDeviceName}“.";
    }

    [RelayCommand]
    private void OpenCableDownload() => _dialogs.OpenUrl(CableDownloadUrl);

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.RevealInExplorer(AppPaths.DataDirectory);

    [RelayCommand]
    private void ClearCache()
    {
        _services.Engine.Cache.Clear();
        OnPropertyChanged(nameof(CacheUsageText));
    }

    // ================= Gerätewahl =================

    partial void OnSelectedVirtualDeviceChanged(AudioDevice? value)
    {
        if (_loading || value is null) return;
        Settings.VirtualOutputDeviceId = value.Id;
        ApplyToEngine();
    }

    partial void OnSelectedMonitorDeviceChanged(AudioDevice? value)
    {
        if (_loading || value is null) return;
        Settings.MonitorOutputDeviceId = value.Id;
        ApplyToEngine();
    }

    partial void OnSelectedMicDeviceChanged(AudioDevice? value)
    {
        if (_loading || value is null) return;
        Settings.MicDeviceId = value.Id;
        ApplyToEngine();
    }

    // ================= Hilfsmittel =================

    private static AudioDevice? Match(IEnumerable<AudioDevice> devices, string? id)
    {
        var list = devices.ToList();
        return list.FirstOrDefault(d => d.Id == id) ?? list.FirstOrDefault();
    }

    private void Set<T>(Action<T> assign, T value, T current, string? alsoNotify = null,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current)) return;

        assign(value);
        OnPropertyChanged(propertyName);
        if (alsoNotify is not null) OnPropertyChanged(alsoNotify);

        if (_loading) return;
        ApplyToEngine();
    }

    private void ApplyToEngine()
    {
        _services.Engine.Apply(Settings);
        _services.SaveSettingsSoon();
        OnPropertyChanged(nameof(CacheUsageText));
        _statusChanged();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(VirtualOutputEnabled));
        OnPropertyChanged(nameof(MonitorEnabled));
        OnPropertyChanged(nameof(MicPassthroughEnabled));
        OnPropertyChanged(nameof(MicMonitorEnabled));
        OnPropertyChanged(nameof(MicMonitorHint));
        OnPropertyChanged(nameof(MasterVolume));
        OnPropertyChanged(nameof(MonitorVolume));
        OnPropertyChanged(nameof(MicVolume));
        OnPropertyChanged(nameof(MasterVolumeText));
        OnPropertyChanged(nameof(MonitorVolumeText));
        OnPropertyChanged(nameof(MicVolumeText));
        OnPropertyChanged(nameof(BufferMilliseconds));
        OnPropertyChanged(nameof(BufferText));
        OnPropertyChanged(nameof(MicLatencyText));
        OnPropertyChanged(nameof(CacheBudgetMegabytes));
        OnPropertyChanged(nameof(CacheText));
        OnPropertyChanged(nameof(CacheUsageText));
        OnPropertyChanged(nameof(HotkeysEnabled));
        OnPropertyChanged(nameof(SuppressHotkeysInOtherApps));
        OnPropertyChanged(nameof(StopOthersOnPlay));
        OnPropertyChanged(nameof(StopAllHotkeyText));
        OnPropertyChanged(nameof(DesktopTunnelHotkeyText));
    }
}
