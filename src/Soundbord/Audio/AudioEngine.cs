using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundbord.Models;

namespace Soundbord.Audio;

/// <summary>Momentaufnahme der laufenden Wiedergabe für die Anzeige.</summary>
/// <param name="ActiveCount">Wie viele Sounds insgesamt laufen.</param>
public sealed record PlaybackStatus(
    Guid SoundId,
    string Name,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPaused,
    bool IsLooping,
    int ActiveCount);

/// <summary>
/// Der Audio-Graph der Anwendung.
///
/// <code>
///   Sounds ──► SoundMixer ──► Sound-Pegel ──┐
///                                           ├──► RouteMixer ──► WASAPI ► CABLE Input
///   Mikrofon ──────────────► Mic-Pegel  ────┘                            (Discord hört das)
///
///   Sounds ──► SoundMixer ──► Sound-Pegel ──► RouteMixer ──► WASAPI ► Kopfhörer
/// </code>
///
/// Zwei Dinge sind hier wesentlich:
///
/// Erstens die <b>zweistufige Mischung</b>. Der Pegelregler für die Sounds sitzt
/// <i>vor</i> dem Zusammenführen mit dem Mikrofon, nicht danach. Säße er dahinter,
/// würde Lauterdrehen der Sounds auch die Stimme lauter machen.
///
/// Zweitens die <b>getrennten Ausgänge</b> statt eines Splitters: die Geräte laufen
/// mit eigenen Taktgebern, und das Mikrofon gehört ausschließlich in den virtuellen Zweig.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly object _activeGate = new();
    private readonly Dictionary<Guid, Playback> _active = [];

    private OutputRoute? _virtualRoute;
    private OutputRoute? _monitorRoute;
    private MicPassthrough? _mic;
    private DesktopTunnel? _desktop;
    private AppSettings _settings = new();
    private bool _micInVirtualRoute;

    /// <summary>Soll der Desktop-Tunnel laufen? Überlebt einen Neuaufbau der Zweige.</summary>
    private bool _desktopWanted;

    private long _sequence;
    private bool _disposed;

    public AudioEngine()
    {
        try { MediaFoundationApi.Startup(); }
        catch (Exception) { /* ohne MF fehlen nur m4a/wma – wav/mp3/ogg gehen weiter */ }
    }

    public SampleCache Cache { get; } = new();

    /// <summary>Feuert im Audio-Thread bzw. UI-Thread – Empfänger müssen marshallen.</summary>
    public event Action<Guid, bool>? SoundStateChanged;

    public event Action? RoutesChanged;

    /// <summary>Feuert, wenn der Desktop-Tunnel an- oder ausgeht – auch von selbst.</summary>
    public event Action? DesktopTunnelChanged;

    public bool VirtualRouteActive => _virtualRoute is not null;
    public bool MonitorRouteActive => _monitorRoute is not null;
    public bool MicActive => _mic is not null;

    /// <summary>Liegt das Mikrofon zusätzlich auf den Kopfhörern?</summary>
    public bool MicMonitorActive => _mic?.IsMonitored ?? false;

    /// <summary>
    /// Erreicht das Mikrofon tatsächlich den virtuellen Ausgang? Falsch, solange dort
    /// kein virtuelles Kabel liegt – dann wird das Durchschleifen bewusst ausgelassen.
    /// </summary>
    public bool MicReachesVirtualOutput => _mic is not null && _micInVirtualRoute;

    public string? VirtualRouteDeviceName => _virtualRoute?.DeviceName;
    public string? MonitorRouteDeviceName => _monitorRoute?.DeviceName;
    public string? MicDeviceName => _mic?.DeviceName;

    /// <summary>Letzter Fehler beim Öffnen eines Geräts, für die Statusanzeige.</summary>
    public string? LastError { get; private set; }

    // ================= Desktop-Ton =================

    /// <summary>Läuft der Tunnel gerade?</summary>
    public bool DesktopTunnelActive => _desktop is not null;

    /// <summary>Was gerade abgegriffen wird, bzw. warum nicht.</summary>
    public string? DesktopTunnelNote { get; private set; }

    /// <summary>
    /// Schaltet den Desktop-Ton in das virtuelle Kabel. Bewusst nicht in
    /// <see cref="AppSettings"/> gespeichert: der Tunnel ist ein temporärer Zustand,
    /// und ein beim Start unbemerkt mitlaufender Mitschnitt des ganzen Systems wäre
    /// eine unangenehme Überraschung.
    /// </summary>
    public bool SetDesktopTunnel(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return false;

            _desktopWanted = enabled;

            if (!enabled)
            {
                CloseDesktopTunnel();
            }
            else if (_desktop is null)
            {
                OpenDesktopTunnel();
            }
        }

        DesktopTunnelChanged?.Invoke();
        return DesktopTunnelActive;
    }

    public bool ToggleDesktopTunnel() => SetDesktopTunnel(!DesktopTunnelActive);

    /// <summary>
    /// Der Desktop-Ton geht ausschließlich in das virtuelle Kabel, niemals auf die
    /// Kopfhörer: dort läuft er bereits – er kommt ja von dort. Ihn zusätzlich
    /// mitzuhören hieße, alles doppelt und leicht versetzt zu hören.
    /// </summary>
    private void OpenDesktopTunnel()
    {
        CloseDesktopTunnel();

        if (_virtualRoute is null)
        {
            DesktopTunnelNote = "Kein virtueller Ausgang aktiv – es gäbe niemanden, der den Desktop-Ton hört.";
            return;
        }

        if (!VirtualCableDetector.IsVirtualCableDevice(_virtualRoute.DeviceName))
        {
            DesktopTunnelNote = $"„{_virtualRoute.DeviceName}“ ist kein virtuelles Kabel. Der Desktop-Ton " +
                                "würde nur auf deinen eigenen Lautsprechern landen – ein zweites Mal.";
            return;
        }

        // Gespeichert ist der Prozessname; die Prozess-ID wird erst jetzt bestimmt.
        // Eine ID von gestern zeigt heute auf irgendein anderes Programm.
        DesktopCaptureTarget target;
        var appKey = _settings.DesktopTunnelApp;

        if (string.IsNullOrWhiteSpace(appKey))
        {
            target = DesktopCaptureTarget.WholeDesktop;
        }
        else
        {
            var app = RunningApps.List().FirstOrDefault(
                a => string.Equals(a.Key, appKey, StringComparison.OrdinalIgnoreCase));

            if (app is null)
            {
                DesktopTunnelNote = $"„{appKey}“ läuft gerade nicht. Starte die Anwendung " +
                                    "und versuche es erneut.";
                return;
            }

            target = new DesktopCaptureTarget(app.ProcessId, app.Name);
        }

        try
        {
            var capture = DesktopCapture.Open(target, _settings.MonitorOutputDeviceId, out var note);

            var backlogMs = MaxMicBacklogMs(_settings.BufferMilliseconds);
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
                BufferDuration = TimeSpan.FromMilliseconds(Math.Max(250, backlogMs * 4)),
            };

            var chain = AudioFormat.Normalize(buffer.ToSampleProvider());
            var volume = new VolumeSampleProvider(chain)
            {
                Volume = VolumeCurve.ToAmplitude(_settings.DesktopVolume),
            };

            var tap = new CaptureTap(buffer, volume, _virtualRoute.RouteMixer, backlogMs);

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                tap.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                tap.TrimBacklog();
            };

            _virtualRoute.RouteMixer.AddMixerInput(volume);
            _desktop = new DesktopTunnel(capture, tap);

            DesktopTunnelNote = note ?? capture.Kind switch
            {
                DesktopCaptureKind.SingleApp =>
                    $"Nur der Ton von „{capture.SourceName}“ läuft ins Kabel.",
                DesktopCaptureKind.ExcludingSelf =>
                    "Der gesamte Systemton läuft ins Kabel – Soundbord selbst ausgenommen.",
                _ => $"„{capture.SourceName}“ wird komplett mitgeschnitten.",
            };
        }
        catch (Exception ex)
        {
            DesktopTunnelNote = $"Desktop-Ton: {ex.Message}";
            CloseDesktopTunnel();
        }
    }

    private void CloseDesktopTunnel()
    {
        _desktop?.Dispose();
        _desktop = null;
    }

    /// <summary>Das interne Rechenformat.</summary>
    public static string InternalFormatText =>
        $"{AudioFormat.SampleRate} Hz · Stereo · 32 Bit Float";

    /// <summary>Format des virtuellen Ausgangs – stimmt es überein, wird nichts umgerechnet.</summary>
    public string? VirtualRouteFormatText => _virtualRoute?.FormatText;

    /// <summary>Hat der Übersteuerungsschutz seit der letzten Abfrage eingegriffen?</summary>
    public bool ConsumeLimiterEngaged()
    {
        var virtualEngaged = _virtualRoute?.Limiter.ConsumeEngaged() ?? false;
        var monitorEngaged = _monitorRoute?.Limiter.ConsumeEngaged() ?? false;
        return virtualEngaged || monitorEngaged;
    }

    // ================= Konfiguration =================

    /// <summary>
    /// Übernimmt Einstellungen. Reine Pegeländerungen laufen ohne Neuaufbau,
    /// Gerätewechsel bauen die betroffenen Zweige neu auf.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        var tunnelReopened = false;

        lock (_gate)
        {
            if (_disposed) return;

            var previous = _settings;
            _settings = settings.Clone();

            // Eine andere Zielanwendung heißt: neuer Abgriff. Das betrifft nur den
            // Tunnel, nicht den restlichen Graphen – ein Neuaufbau wäre zu grob.
            if (previous.DesktopTunnelApp != _settings.DesktopTunnelApp && _desktopWanted)
            {
                OpenDesktopTunnel();
                tunnelReopened = true;
            }

            Cache.BudgetBytes = Math.Max(16, _settings.CacheBudgetMegabytes) * 1024L * 1024L;
            Cache.MaxCacheSeconds = Math.Max(5, _settings.MaxCacheSeconds);

            var structural =
                previous.VirtualOutputDeviceId != _settings.VirtualOutputDeviceId ||
                previous.MonitorOutputDeviceId != _settings.MonitorOutputDeviceId ||
                previous.MicDeviceId != _settings.MicDeviceId ||
                previous.VirtualOutputEnabled != _settings.VirtualOutputEnabled ||
                previous.MonitorEnabled != _settings.MonitorEnabled ||
                previous.MicPassthroughEnabled != _settings.MicPassthroughEnabled ||
                previous.MicMonitorEnabled != _settings.MicMonitorEnabled ||
                previous.BufferMilliseconds != _settings.BufferMilliseconds ||
                (_virtualRoute is null && _monitorRoute is null);

            if (structural)
                Rebuild();
            else
                ApplyVolumes();
        }

        if (tunnelReopened) DesktopTunnelChanged?.Invoke();
    }

    /// <summary>
    /// Sound- und Mikrofonpegel sind vollständig unabhängig: der Sound-Pegel greift
    /// am Untermischer, der Mikrofonpegel am eigenen Abgriff.
    ///
    /// Alle Reglerstellungen laufen durch <see cref="VolumeCurve"/> – gespeichert und
    /// angezeigt wird die Reglerstellung, im Graphen landet der Verstärkungsfaktor.
    /// </summary>
    private void ApplyVolumes()
    {
        if (_virtualRoute is not null)
            _virtualRoute.SoundVolume.Volume = VirtualSoundAmplitude();

        if (_monitorRoute is not null)
            _monitorRoute.SoundVolume.Volume = MonitorSoundAmplitude();

        _mic?.SetVolume(VolumeCurve.ToAmplitude(_settings.MicVolume));
        _desktop?.SetVolume(VolumeCurve.ToAmplitude(_settings.DesktopVolume));

        // Der Schutz lässt sich im laufenden Betrieb umschalten – kein Neuaufbau nötig.
        if (_virtualRoute is not null) _virtualRoute.Limiter.Enabled = _settings.LimiterEnabled;
        if (_monitorRoute is not null) _monitorRoute.Limiter.Enabled = _settings.LimiterEnabled;
    }

    /// <summary>Liest das Mischformat des Geräts für die Anzeige aus.</summary>
    private static string DescribeFormat(MMDevice device)
    {
        try
        {
            var mix = device.AudioClient.MixFormat;
            var channels = mix.Channels == 1 ? "Mono" : mix.Channels == 2 ? "Stereo" : $"{mix.Channels} Kanäle";
            return $"{mix.SampleRate} Hz · {channels} · {mix.BitsPerSample} Bit";
        }
        catch (Exception)
        {
            return "Format unbekannt";
        }
    }

    private float VirtualSoundAmplitude() => VolumeCurve.ToAmplitude(_settings.MasterVolume);

    /// <summary>Zwei Regler in Reihe: Faktoren multiplizieren heißt Dezibel addieren.</summary>
    private float MonitorSoundAmplitude() =>
        VolumeCurve.ToAmplitude(_settings.MasterVolume) * VolumeCurve.ToAmplitude(_settings.MonitorVolume);

    // ================= Latenzbudget =================

    /// <summary>Puffer der Aufnahme. NAudios Vorgabe von 100 ms ist für Sprache viel zu träge.</summary>
    public static int CaptureBufferMs(int bufferMs) => Math.Clamp(bufferMs / 2, 10, 40);

    /// <summary>
    /// Zulässiger Rückstand im Zwischenpuffer, bevor nachgeregelt wird. Ohne diese
    /// Grenze wächst er durch Taktdrift zwischen Mikrofon und Ausgabegerät immer
    /// weiter – die Verzögerung baut sich schleichend auf und verschwindet nie mehr.
    /// </summary>
    public static int MaxMicBacklogMs(int bufferMs) => Math.Clamp(bufferMs, 20, 60);

    public static int EstimateMicLatencyMs(int bufferMs) =>
        CaptureBufferMs(bufferMs) + MaxMicBacklogMs(bufferMs) + bufferMs;

    private void Rebuild()
    {
        StopAllInternal();

        // Der Tunnel hängt am virtuellen Zweig, der hier gleich verschwindet.
        CloseDesktopTunnel();

        _mic?.Dispose();
        _mic = null;
        _virtualRoute?.Dispose();
        _virtualRoute = null;
        _monitorRoute?.Dispose();
        _monitorRoute = null;

        LastError = null;

        if (_settings.VirtualOutputEnabled)
        {
            _virtualRoute = OpenRoute(_settings.VirtualOutputDeviceId, VirtualSoundAmplitude(), "Virtuelles Kabel");
        }

        if (_settings.MonitorEnabled && !MonitorWouldDuplicate())
        {
            _monitorRoute = OpenRoute(
                _settings.MonitorOutputDeviceId,
                MonitorSoundAmplitude(),
                "Mithören");
        }

        _micInVirtualRoute = false;

        if (_settings.MicPassthroughEnabled)
        {
            var targets = new List<MixingSampleProvider>(2);

            // Das Mikrofon gehört ausschließlich in ein virtuelles Kabel. Zeigt der
            // virtuelle Ausgang auf ein echtes Wiedergabegerät – der Normalfall,
            // solange kein Kabel eingerichtet ist – dann würde das Durchschleifen
            // die eigene Stimme auf die eigenen Kopfhörer legen. Bei Lautsprechern
            // entstünde daraus eine Rückkopplungsschleife.
            if (_virtualRoute is not null)
            {
                if (VirtualCableDetector.IsVirtualCableDevice(_virtualRoute.DeviceName))
                {
                    targets.Add(_virtualRoute.RouteMixer);
                    _micInVirtualRoute = true;
                }
                else
                {
                    LastError = $"Mikrofon nicht durchgeschleift: „{_virtualRoute.DeviceName}“ " +
                                "ist kein virtuelles Kabel.";
                }
            }

            // Sich selbst mithören ist eine bewusste Entscheidung des Nutzers und
            // deshalb von der Kabelprüfung ausgenommen.
            if (_settings.MicMonitorEnabled && _monitorRoute is not null)
                targets.Add(_monitorRoute.RouteMixer);

            if (targets.Count > 0)
                _mic = OpenMic(_settings.MicDeviceId, targets, VolumeCurve.ToAmplitude(_settings.MicVolume));
        }

        // War der Tunnel an, hängt er sich an den neuen Zweig.
        if (_desktopWanted) OpenDesktopTunnel();

        RoutesChanged?.Invoke();
        DesktopTunnelChanged?.Invoke();
    }

    /// <summary>
    /// Zeigen virtueller Ausgang und Mithören auf dasselbe Gerät, käme jeder Sound
    /// dort zweimal an – doppelt so laut und mit Kammfilter-Effekt durch die minimal
    /// versetzten Clocks. Das ist der Normalfall, solange kein Kabel eingerichtet ist.
    /// </summary>
    private bool MonitorWouldDuplicate()
    {
        if (_virtualRoute is null) return false;

        using var monitor = AudioDevices.Resolve(_settings.MonitorOutputDeviceId, capture: false);
        if (monitor is null) return false;

        if (monitor.ID != _virtualRoute.DeviceId) return false;

        LastError = "Mithören übersprungen: identisch mit dem virtuellen Ausgang.";
        return true;
    }

    private OutputRoute? OpenRoute(string? deviceId, float volume, string label)
    {
        MMDevice? device = null;
        try
        {
            device = AudioDevices.Resolve(deviceId, capture: false);
            if (device is null)
            {
                LastError = $"{label}: kein Wiedergabegerät gefunden.";
                return null;
            }

            // Stufe 1: nur die Sounds, mit eigenem Pegel.
            var soundMixer = new MixingSampleProvider(AudioFormat.Engine) { ReadFully = true };
            var soundVolume = new VolumeSampleProvider(soundMixer) { Volume = volume };

            // Stufe 2: Sounds + Mikrofon. Was hier ankommt, ist bereits fertig gepegelt.
            var routeMixer = new MixingSampleProvider(AudioFormat.Engine) { ReadFully = true };
            routeMixer.AddMixerInput(soundVolume);

            // Stufe 3: letzte Instanz vor dem Gerät. Nur hier kann die Summe aus
            // Sounds und Mikrofon den zulässigen Bereich verlassen.
            var limiter = new SoftLimiterSampleProvider(routeMixer) { Enabled = _settings.LimiterEnabled };

            var output = new WasapiOut(
                device,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: Math.Clamp(_settings.BufferMilliseconds, 10, 400));

            output.Init(limiter);
            output.Play();

            var route = new OutputRoute(
                device, output, soundMixer, soundVolume, routeMixer, limiter,
                device.FriendlyName, device.ID, DescribeFormat(device));
            device = null; // Besitz ist übergegangen
            return route;
        }
        catch (Exception ex)
        {
            LastError = $"{label}: {ex.Message}";
            device?.Dispose();
            return null;
        }
    }

    private MicPassthrough? OpenMic(string? deviceId, IReadOnlyList<MixingSampleProvider> targets, float volume)
    {
        MMDevice? device = null;
        WasapiCapture? capture = null;
        try
        {
            device = AudioDevices.Resolve(deviceId, capture: true);
            if (device is null)
            {
                LastError = "Mikrofon: kein Aufnahmegerät gefunden.";
                return null;
            }

            // Nicht das Kabel selbst aufnehmen – das ergäbe eine Rückkopplungsschleife.
            if (VirtualCableDetector.IsVirtualCableDevice(device.FriendlyName))
            {
                LastError = "Mikrofon: virtuelles Kabel als Eingang ausgewählt (Rückkopplung). Übersprungen.";
                device.Dispose();
                return null;
            }

            // NAudios Vorgabe wären 100 ms Aufnahmepuffer – für Sprache untragbar.
            var captureMs = CaptureBufferMs(_settings.BufferMilliseconds);
            capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: captureMs)
            {
                ShareMode = AudioClientShareMode.Shared,
            };

            // Ein eigener Abgriff je Ziel. Zwei Mixer dürfen sich keinen Puffer
            // teilen: jeder Read verbraucht die Samples, sie würden sich das
            // Signal gegenseitig wegnehmen und beide nur Bruchstücke hören.
            var backlogMs = MaxMicBacklogMs(_settings.BufferMilliseconds);
            var taps = new List<CaptureTap>(targets.Count);

            foreach (var target in targets)
            {
                var buffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                    BufferDuration = TimeSpan.FromMilliseconds(Math.Max(250, backlogMs * 4)),
                };

                var chain = AudioFormat.Normalize(buffer.ToSampleProvider());
                var volumeProvider = new VolumeSampleProvider(chain) { Volume = volume };

                target.AddMixerInput(volumeProvider);
                taps.Add(new CaptureTap(buffer, volumeProvider, target, backlogMs));
            }

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;

                foreach (var tap in taps)
                {
                    tap.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    tap.TrimBacklog();
                }
            };

            capture.StartRecording();

            var mic = new MicPassthrough(device, capture, taps, device.FriendlyName);
            device = null;
            capture = null;
            return mic;
        }
        catch (Exception ex)
        {
            LastError = $"Mikrofon: {ex.Message}";
            capture?.Dispose();
            device?.Dispose();
            return null;
        }
    }

    // ================= Wiedergabe =================

    public bool IsPlaying(Guid soundId)
    {
        lock (_activeGate) return _active.ContainsKey(soundId);
    }

    /// <summary>
    /// Zustand der zuletzt gestarteten Wiedergabe für die Anzeige. Wird vom UI in
    /// kurzen Abständen abgefragt statt per Ereignis gemeldet: die Position ändert
    /// sich fortlaufend im Audio-Thread, und dort für jedes Sample ein Ereignis
    /// auszulösen wäre absurd.
    /// </summary>
    public PlaybackStatus? GetStatus()
    {
        lock (_activeGate)
        {
            if (_active.Count == 0) return null;

            Playback? newest = null;
            foreach (var playback in _active.Values)
            {
                if (newest is null || playback.Sequence > newest.Sequence) newest = playback;
            }

            return newest?.ToStatus(_active.Count);
        }
    }

    public void SetPaused(Guid soundId, bool paused)
    {
        Playback? playback;
        lock (_activeGate) _active.TryGetValue(soundId, out playback);
        playback?.SetPaused(paused);
    }

    /// <summary>Schaltet die Pause um und liefert den neuen Zustand.</summary>
    public bool TogglePause(Guid soundId)
    {
        Playback? playback;
        lock (_activeGate) _active.TryGetValue(soundId, out playback);
        if (playback is null) return false;

        var paused = !playback.IsPaused;
        playback.SetPaused(paused);
        return paused;
    }

    /// <summary>Startet den Sound bzw. stoppt ihn, wenn er schon läuft. Gibt den neuen Zustand zurück.</summary>
    public bool Toggle(SoundEntry entry)
    {
        if (IsPlaying(entry.Id))
        {
            Stop(entry.Id);
            return false;
        }

        return Play(entry);
    }

    public bool Play(SoundEntry entry)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_virtualRoute is null && _monitorRoute is null) return false;

            if (_settings.StopOthersOnPlay) StopAllInternal();
            else Stop(entry.Id);

            var cached = Cache.GetOrLoad(entry.FilePath);
            if (cached is null && !File.Exists(entry.FilePath)) return false;

            var playback = new Playback(entry.Id, entry.Name, entry.Loop, Interlocked.Increment(ref _sequence));
            var volume = VolumeCurve.ToAmplitude(Math.Clamp(entry.Volume, 0f, 2f));

            if (_virtualRoute is not null) AttachVoice(playback, _virtualRoute, entry, cached, volume);
            if (_monitorRoute is not null) AttachVoice(playback, _monitorRoute, entry, cached, volume);

            if (playback.VoiceCount == 0) return false;

            lock (_activeGate) _active[entry.Id] = playback;

            // Wichtig: nur aufräumen, wenn wir noch die aktuelle Wiedergabe sind.
            // Ein schnell erneut getriggerter Sound läuft sonst Gefahr, dass die
            // ausblendende Vorgänger-Instanz seinen Eintrag wieder herauswirft.
            playback.Completed += id =>
            {
                bool wasCurrent;
                lock (_activeGate)
                {
                    wasCurrent = _active.TryGetValue(id, out var current) && ReferenceEquals(current, playback);
                    if (wasCurrent) _active.Remove(id);
                }

                if (wasCurrent) SoundStateChanged?.Invoke(id, false);
            };

            playback.Arm();

            SoundStateChanged?.Invoke(entry.Id, true);
            return true;
        }
    }

    private void AttachVoice(Playback playback, OutputRoute route, SoundEntry entry, CachedSound? cached, float volume)
    {
        try
        {
            ISampleProvider source;
            IDisposable? owner = null;

            if (cached is not null)
            {
                source = new CachedSoundSource(cached, entry.Loop);
            }
            else
            {
                var streaming = new StreamingSource(entry.FilePath, entry.Loop);
                source = streaming;
                owner = streaming;
            }

            var voice = new Voice(source, owner, volume);
            playback.Add(voice);
            route.SoundMixer.AddMixerInput(voice);
        }
        catch (Exception ex)
        {
            LastError = $"Wiedergabe: {ex.Message}";
        }
    }

    public void Stop(Guid soundId)
    {
        Playback? playback;
        lock (_activeGate) _active.TryGetValue(soundId, out playback);
        playback?.Stop();
    }

    public void StopAll()
    {
        lock (_gate) StopAllInternal();
    }

    private void StopAllInternal()
    {
        Playback[] all;
        lock (_activeGate) all = _active.Values.ToArray();
        foreach (var playback in all) playback.Stop();
    }

    /// <param name="position">Reglerstellung, nicht Verstärkungsfaktor.</param>
    public void SetSoundVolume(Guid soundId, float position)
    {
        Playback? playback;
        lock (_activeGate) _active.TryGetValue(soundId, out playback);
        playback?.SetVolume(VolumeCurve.ToAmplitude(Math.Clamp(position, 0f, 2f)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            StopAllInternal();
            CloseDesktopTunnel();
            _mic?.Dispose();
            _virtualRoute?.Dispose();
            _monitorRoute?.Dispose();
            _mic = null;
            _virtualRoute = null;
            _monitorRoute = null;
            Cache.Clear();
        }

        try { MediaFoundationApi.Shutdown(); }
        catch (Exception) { /* egal beim Herunterfahren */ }
    }

    // ================= interne Typen =================

    private sealed class OutputRoute(
        MMDevice device,
        WasapiOut output,
        MixingSampleProvider soundMixer,
        VolumeSampleProvider soundVolume,
        MixingSampleProvider routeMixer,
        SoftLimiterSampleProvider limiter,
        string deviceName,
        string deviceId,
        string formatText) : IDisposable
    {
        public SoftLimiterSampleProvider Limiter { get; } = limiter;

        /// <summary>Das tatsächliche Format des Geräts – zum Abgleich mit dem internen.</summary>
        public string FormatText { get; } = formatText;

        /// <summary>Hier kommen die Sounds hinein.</summary>
        public MixingSampleProvider SoundMixer { get; } = soundMixer;

        /// <summary>Pegel der Sounds – wirkt nicht auf das Mikrofon.</summary>
        public VolumeSampleProvider SoundVolume { get; } = soundVolume;

        /// <summary>Hier kommt das Mikrofon hinein, nach dem Sound-Pegel.</summary>
        public MixingSampleProvider RouteMixer { get; } = routeMixer;

        public string DeviceName { get; } = deviceName;
        public string DeviceId { get; } = deviceId;

        public void Dispose()
        {
            try { output.Stop(); } catch (Exception) { /* Gerät evtl. schon weg */ }
            try { output.Dispose(); } catch (Exception) { }
            try { device.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>Ein Abgriff eines Aufnahmesignals für genau einen Mixer.</summary>
    private sealed class CaptureTap(
        BufferedWaveProvider buffer,
        VolumeSampleProvider volume,
        MixingSampleProvider target,
        int maxBacklogMs)
    {
        private readonly int _maxBacklogBytes = buffer.WaveFormat.AverageBytesPerSecond * maxBacklogMs / 1000;
        private byte[] _scratch = [];

        public BufferedWaveProvider Buffer { get; } = buffer;
        public VolumeSampleProvider Volume { get; } = volume;
        public MixingSampleProvider Target { get; } = target;

        /// <summary>
        /// Hält den Rückstand klein. Mikrofon und Ausgabegerät laufen auf getrennten
        /// Taktgebern; nimmt das Mikrofon auch nur Promille schneller auf, als die
        /// Ausgabe abholt, füllt sich der Puffer unaufhaltsam. Die Verzögerung würde
        /// sich über Minuten aufbauen und nie wieder abbauen.
        ///
        /// Verworfen wird nur der Überschuss, nicht der ganze Puffer – so bleibt die
        /// Stimme fortlaufend und es fehlen allenfalls wenige Millisekunden.
        /// </summary>
        public void TrimBacklog()
        {
            var excess = Buffer.BufferedBytes - _maxBacklogBytes;
            if (excess <= 0) return;

            excess -= excess % Buffer.WaveFormat.BlockAlign;
            if (excess <= 0) return;

            if (_scratch.Length < excess) _scratch = new byte[excess];
            Buffer.Read(_scratch, 0, excess);
        }
    }

    /// <summary>Der Desktop-Ton auf dem Weg in das virtuelle Kabel.</summary>
    private sealed class DesktopTunnel(DesktopCapture capture, CaptureTap tap) : IDisposable
    {
        public void SetVolume(float volume) => tap.Volume.Volume = volume;

        public void Dispose()
        {
            // Erst die Quelle stilllegen, dann den Abgriff aus dem Mixer nehmen –
            // andernfalls schriebe der Aufnahme-Thread noch in einen toten Puffer.
            try { capture.Dispose(); } catch (Exception) { }
            try { tap.Target.RemoveMixerInput(tap.Volume); } catch (Exception) { }
            tap.Buffer.ClearBuffer();
        }
    }

    private sealed class MicPassthrough(
        MMDevice device,
        WasapiCapture capture,
        IReadOnlyList<CaptureTap> taps,
        string deviceName) : IDisposable
    {
        public string DeviceName { get; } = deviceName;

        /// <summary>Liegt das Mikrofon auch auf den Kopfhörern?</summary>
        public bool IsMonitored => taps.Count > 1;

        public void SetVolume(float volume)
        {
            foreach (var tap in taps) tap.Volume.Volume = volume;
        }

        public void Dispose()
        {
            try { capture.StopRecording(); } catch (Exception) { }
            try { capture.Dispose(); } catch (Exception) { }

            foreach (var tap in taps)
            {
                try { tap.Target.RemoveMixerInput(tap.Volume); } catch (Exception) { }
                tap.Buffer.ClearBuffer();
            }

            try { device.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>Ein Sound, der gleichzeitig in mehreren Zweigen läuft.</summary>
    private sealed class Playback(Guid soundId, string name, bool loop, long sequence)
    {
        private readonly List<Voice> _voices = [];
        private bool _paused;

        public long Sequence { get; } = sequence;

        public bool IsPaused => _paused;

        public void SetPaused(bool paused)
        {
            _paused = paused;
            foreach (var voice in _voices) voice.SetPaused(paused);
        }

        /// <summary>
        /// Die erste Stimme führt: Alle Zweige spielen dieselbe Quelle synchron,
        /// eine davon genügt für Position und Länge.
        /// </summary>
        public PlaybackStatus ToStatus(int activeCount)
        {
            var lead = _voices.Count > 0 ? _voices[0] : null;

            return new PlaybackStatus(
                soundId,
                name,
                lead?.Position ?? TimeSpan.Zero,
                lead?.Duration ?? TimeSpan.Zero,
                _paused,
                loop,
                activeCount);
        }


        // Startet bei 1: dieses "Konstruktions-Token" hält den Zähler oben, solange
        // noch Voices hinzukommen. Ohne das könnte eine sehr kurze Datei im ersten
        // Zweig fertig sein, bevor der zweite Zweig überhaupt angehängt wurde.
        private int _remaining = 1;
        private int _completedFired;

        public int VoiceCount => _voices.Count;

        public event Action<Guid>? Completed;

        public void Add(Voice voice)
        {
            _voices.Add(voice);
            Interlocked.Increment(ref _remaining);
            voice.Finished += (_, _) => Decrement();
        }

        /// <summary>Gibt das Konstruktions-Token frei.</summary>
        public void Arm() => Decrement();

        public void Stop()
        {
            foreach (var voice in _voices) voice.Stop();
        }

        public void SetVolume(float volume)
        {
            foreach (var voice in _voices) voice.Volume = volume;
        }

        private void Decrement()
        {
            if (Interlocked.Decrement(ref _remaining) == 0 &&
                Interlocked.Exchange(ref _completedFired, 1) == 0)
            {
                Completed?.Invoke(soundId);
            }
        }
    }
}
