namespace Soundbord.Models;

/// <summary>
/// Persistente Einstellungen. Liegt als lesbares JSON in %APPDATA%\Soundbord\settings.json.
/// </summary>
public sealed class AppSettings
{
    // --- Routing -------------------------------------------------------

    /// <summary>Wiedergabegerät, das die Zielanwendung als Mikrofon sieht (i.d.R. "CABLE Input").</summary>
    public string? VirtualOutputDeviceId { get; set; }

    /// <summary>Mithörgerät – deine Kopfhörer. Getrennt vom virtuellen Kabel.</summary>
    public string? MonitorOutputDeviceId { get; set; }

    /// <summary>Physisches Mikrofon, das in das virtuelle Kabel durchgeschleift wird.</summary>
    public string? MicDeviceId { get; set; }

    public bool VirtualOutputEnabled { get; set; } = true;
    public bool MonitorEnabled { get; set; } = true;
    public bool MicPassthroughEnabled { get; set; } = true;

    /// <summary>
    /// Das eigene Mikrofon zusätzlich auf die Kopfhörer legen. Standardmäßig aus:
    /// der Weg über WASAPI hinein und wieder hinaus kostet rund 60 ms, und das
    /// eigene Echo in dieser Verzögerung macht das Sprechen sehr unangenehm.
    /// </summary>
    public bool MicMonitorEnabled { get; set; }

    // --- Pegel ---------------------------------------------------------

    // Reglerstellungen, keine Verstärkungsfaktoren: 1.0 = unverändert (0 dB).
    // Die Umrechnung macht VolumeCurve.
    public float MasterVolume { get; set; } = 1.00f;
    public float MonitorVolume { get; set; } = 0.85f;
    public float MicVolume { get; set; } = 1.00f;

    /// <summary>Pegel des durchgereichten Desktop-Tons. Eigener Weg, eigener Regler.</summary>
    public float DesktopVolume { get; set; } = 0.85f;

    // --- Desktop-Ton ---------------------------------------------------

    /// <summary>
    /// Hotkey für den Desktop-Ton-Tunnel. Der Tunnel ist bewusst nur temporär aktiv,
    /// deshalb ist ein Umschalter aus dem Spiel heraus nützlicher als eine Einstellung.
    /// </summary>
    public Hotkey? DesktopTunnelHotkey { get; set; }

    /// <summary>
    /// Welche Anwendung übertragen wird – als <b>Prozessname</b>, nicht als Prozess-ID:
    /// die ändert sich bei jedem Neustart der Anwendung, der Name bleibt. Leer oder
    /// <c>null</c> bedeutet „alles außer Soundbord“.
    /// </summary>
    public string? DesktopTunnelApp { get; set; }

    // --- Audio ---------------------------------------------------------

    /// <summary>
    /// WASAPI-Puffer in ms. Kleiner = niedrigere Latenz, höheres Aussetzer-Risiko.
    /// Bestimmt zugleich den Aufnahmepuffer und den zulässigen Rückstand des
    /// Mikrofons und geht damit dreifach in dessen Verzögerung ein.
    /// </summary>
    public int BufferMilliseconds { get; set; } = 30;

    /// <summary>
    /// Weiches Abfangen von Übersteuerung. Ohne das schneidet WASAPI alles über
    /// ±1,0 hart ab, was in den lautesten Momenten hörbar knistert.
    ///
    /// Standardmäßig <b>aus</b>: Der Schutz greift in das Signal ein, und solange
    /// nichts übersteuert, ist Nichtstun die ehrlichere Voreinstellung. Wer Knistern
    /// hört, schaltet ihn zu.
    /// </summary>
    public bool LimiterEnabled { get; set; }

    /// <summary>Obergrenze für den PCM-Cache im RAM.</summary>
    public int CacheBudgetMegabytes { get; set; } = 192;

    /// <summary>Sounds länger als das werden von der Platte gestreamt statt gecacht.</summary>
    public int MaxCacheSeconds { get; set; } = 90;

    // --- Hotkeys -------------------------------------------------------

    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>Belegte Tasten für andere Programme schlucken statt nur mitzuhören.</summary>
    public bool SuppressHotkeysInOtherApps { get; set; }

    public Hotkey? StopAllHotkey { get; set; }

    /// <summary>Beim Start eines Sounds alle laufenden stoppen (monophon).</summary>
    public bool StopOthersOnPlay { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
