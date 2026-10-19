using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Soundbord.Audio;

/// <summary>Eine Anwendung, deren Ton sich übertragen lässt.</summary>
/// <param name="Key">
/// Der Prozessname. Bewusst nicht die Prozess-ID: die ändert sich bei jedem Neustart
/// der Anwendung, der Name bleibt. Gespeichert wird deshalb der Name, aufgelöst wird
/// erst beim Öffnen des Tunnels.
/// </param>
/// <param name="ProcessId">
/// Die Prozess-ID des <b>obersten</b> Prozesses der Anwendung. Nur für den laufenden
/// Moment gültig. Der Abgriff nimmt den gesamten Baum darunter mit.
/// </param>
/// <param name="HasAudioSession">
/// Hat die Anwendung das Ausgabegerät bereits geöffnet? Nur ein Hinweis, keine
/// Voraussetzung – der Abgriff funktioniert auch bei einer Anwendung, die noch schweigt.
/// </param>
public sealed record AudioApp(string Key, string Name, int ProcessId, bool HasAudioSession)
{
    /// <summary>Sonderfall „alles außer Soundbord“.</summary>
    public const string WholeDesktopKey = "";

    public static AudioApp WholeDesktop { get; } = new(WholeDesktopKey, "Ganzer Desktop", 0, true);

    public bool IsWholeDesktop => Key.Length == 0;

    /// <summary>Nur ein Platzhalter für eine gemerkte, aber nicht laufende Anwendung.</summary>
    public bool IsOffline => !IsWholeDesktop && ProcessId == 0;
}

/// <summary>
/// Findet die Anwendungen, deren Ton übertragen werden kann.
/// </summary>
/// <remarks>
/// <para>
/// Naheliegend wäre, dafür die WASAPI-Sitzungen abzufragen – jedes Programm, das ein
/// Wiedergabegerät geöffnet hat, hinterlässt dort eine Sitzung. Das ist aber das
/// <b>falsche Signal</b>: Programme wie Spotify öffnen das Gerät erst beim ersten
/// Abspielen. Vor dem ersten Ton stünden sie nicht in der Liste – und genau dann will
/// man sie auswählen.
/// </para>
/// <para>
/// Grundlage ist deshalb die Liste der laufenden Anwendungen <i>mit sichtbarem Fenster</i>,
/// ergänzt um alles, was gerade eine Audiositzung hält (das erfasst auch Programme, die
/// im Hintergrund ohne Fenster tönen). Der Abgriff selbst braucht nur eine gültige
/// Prozess-ID; ob dort schon Ton fließt, ist ihm gleich.
/// </para>
/// </remarks>
public static class RunningApps
{
    /// <summary>
    /// Fensterwirte der Windows-Oberfläche. Sie haben ein Fenster, sind aber nie eine
    /// sinnvolle Tonquelle. Sollte einer wider Erwarten doch Ton ausgeben, wird er
    /// trotzdem aufgenommen – die Liste greift nur bei Stille.
    /// </summary>
    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "TextInputHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "LockApp",
    };

    /// <summary>
    /// Alle auswählbaren Anwendungen, nach Namen sortiert. Soundbord selbst bleibt außen vor.
    /// </summary>
    public static List<AudioApp> List()
    {
        var self = Environment.ProcessId;
        var withAudio = SessionProcessIds(self);
        var groups = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                // PID 0 und 4 sind Leerlauf und Kernel – keine Anwendungen.
                if (process.Id <= 4 || process.Id == self) continue;

                var name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var hasSession = withAudio.Contains(process.Id);
                var hasWindow = process.MainWindowHandle != IntPtr.Zero &&
                                !string.IsNullOrWhiteSpace(process.MainWindowTitle);

                // Alles ohne Fenster und ohne Ton wäre nur Rauschen in der Liste:
                // Dienste, Treiber, Hintergrundprozesse.
                if (!hasSession && !hasWindow) continue;
                if (!hasSession && ShellHosts.Contains(name)) continue;

                var candidate = new Candidate(process.Id, hasWindow, StartTime(process), hasSession);

                if (!groups.TryGetValue(name, out var existing)) groups[name] = candidate;
                else groups[name] = Merge(existing, candidate);
            }
            catch (Exception)
            {
                // Prozess inzwischen beendet oder nicht lesbar – überspringen.
            }
            finally
            {
                process.Dispose();
            }
        }

        var result = groups
            .Select(pair => new AudioApp(
                pair.Key,
                Describe(pair.Value.ProcessId, pair.Key),
                pair.Value.ProcessId,
                pair.Value.HasSession))
            .ToList();

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>
    /// Verschmilzt zwei Prozesse derselben Anwendung. Übertragen wird später der ganze
    /// Prozessbaum, deshalb muss hier der <b>oberste</b> Prozess gewinnen – sonst bliebe
    /// bei einem Browser der Ton der Geschwisterprozesse außen vor.
    ///
    /// Das Fenster ist das beste Anzeichen für den Hauptprozess; gibt es keines, gewinnt
    /// der zuerst gestartete, denn ein Elternprozess läuft vor seinen Kindern.
    /// </summary>
    private static Candidate Merge(Candidate a, Candidate b)
    {
        var winner = a.HasWindow != b.HasWindow
            ? (a.HasWindow ? a : b)
            : (a.Started <= b.Started ? a : b);

        // Eine Sitzung irgendwo im Baum zählt für die ganze Anwendung.
        return winner with { HasSession = a.HasSession || b.HasSession };
    }

    private static DateTime StartTime(Process process)
    {
        try { return process.StartTime; }
        catch (Exception) { return DateTime.MaxValue; }
    }

    /// <summary>Prozess-IDs, die gerade eine Audiositzung auf irgendeinem Gerät halten.</summary>
    private static HashSet<int> SessionProcessIds(int self)
    {
        var result = new HashSet<int>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    Collect(device, self, result);
                }
            }
        }
        catch (Exception)
        {
            // Ohne diese Zusatzinformation ist die Liste nur etwas weniger genau.
        }

        return result;
    }

    private static void Collect(MMDevice device, int self, HashSet<int> result)
    {
        SessionCollection sessions;
        try
        {
            sessions = device.AudioSessionManager.Sessions;
        }
        catch (Exception)
        {
            return; // Gerät antwortet nicht – nächstes.
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            try
            {
                var session = sessions[i];
                if (session.IsSystemSoundsSession) continue;
                if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                var pid = (int)session.GetProcessID;
                if (pid > 4 && pid != self) result.Add(pid);
            }
            catch (Exception)
            {
                // Sitzung nicht lesbar – überspringen.
            }
        }
    }

    /// <summary>
    /// Der Anzeigename. Der Prozessname ist oft kryptisch („msedgewebview2“), die
    /// Dateibeschreibung dagegen lesbar. Sie ist bei Prozessen anderer Nutzer oder mit
    /// höheren Rechten nicht abrufbar – dann bleibt der Prozessname.
    /// </summary>
    private static string Describe(int processId, string fallback)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception)
        {
            // Kein Zugriff auf das Modul – nicht weiter schlimm.
        }

        return fallback;
    }

    private readonly record struct Candidate(int ProcessId, bool HasWindow, DateTime Started, bool HasSession);
}
