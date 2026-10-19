namespace Soundbord.Audio;

public enum VirtualCableKind
{
    None,
    VbCable,
    VoiceMeeter,
    VirtualAudioCable,
}

/// <summary>
/// Ein erkanntes virtuelles Kabel: das Wiedergabegerät, in das wir schreiben,
/// und das Aufnahmegerät, das die Zielanwendung (Discord, TS, ...) auswählen muss.
/// </summary>
public sealed record VirtualCableInfo(
    VirtualCableKind Kind,
    string ProductName,
    AudioDevice RenderDevice,
    AudioDevice? CaptureDevice)
{
    public string CaptureDeviceName => CaptureDevice?.Name ?? "(kein passendes Aufnahmegerät gefunden)";
}

/// <summary>
/// Erkennt installierte virtuelle Audiokabel. VB-Cable hat Vorrang, VoiceMeeter und
/// Virtual Audio Cable werden als Alternativen akzeptiert – wer die schon nutzt,
/// soll nicht zusätzlich VB-Cable installieren müssen.
/// </summary>
public static class VirtualCableDetector
{
    public static VirtualCableInfo? Detect() => Detect(AudioDevices.Render(), AudioDevices.Capture());

    public static VirtualCableInfo? Detect(IReadOnlyList<AudioDevice> render, IReadOnlyList<AudioDevice> capture)
    {
        foreach (var kind in new[] { VirtualCableKind.VbCable, VirtualCableKind.VoiceMeeter, VirtualCableKind.VirtualAudioCable })
        {
            var device = render.FirstOrDefault(d => Matches(kind, d.Name, renderSide: true));
            if (device is null) continue;

            var counterpart = FindCounterpart(kind, device, capture);
            return new VirtualCableInfo(kind, ProductName(kind), device, counterpart);
        }

        return null;
    }

    public static bool IsVirtualCableDevice(string name) =>
        Matches(VirtualCableKind.VbCable, name, true) || Matches(VirtualCableKind.VbCable, name, false) ||
        Matches(VirtualCableKind.VoiceMeeter, name, true) || Matches(VirtualCableKind.VoiceMeeter, name, false) ||
        Matches(VirtualCableKind.VirtualAudioCable, name, true) || Matches(VirtualCableKind.VirtualAudioCable, name, false);

    public static string ProductName(VirtualCableKind kind) => kind switch
    {
        VirtualCableKind.VbCable => "VB-CABLE",
        VirtualCableKind.VoiceMeeter => "VoiceMeeter",
        VirtualCableKind.VirtualAudioCable => "Virtual Audio Cable",
        _ => "unbekannt",
    };

    private static bool Matches(VirtualCableKind kind, string name, bool renderSide)
    {
        var n = name.ToLowerInvariant();

        // Achtung: Aus Sicht der App ist das Wiedergabegerät das "Input" des Kabels.
        var sideToken = renderSide ? "input" : "output";

        return kind switch
        {
            VirtualCableKind.VbCable => n.Contains("cable") && n.Contains("vb-audio") && n.Contains(sideToken),
            VirtualCableKind.VoiceMeeter => n.Contains("voicemeeter") && n.Contains(sideToken),
            VirtualCableKind.VirtualAudioCable => n.Contains("virtual audio cable") && (renderSide ? !n.Contains("out") : n.Contains("out")),
            _ => false,
        };
    }

    /// <summary>
    /// Sucht das Gegenstück auf der Aufnahmeseite. Primär über den Treibernamen in
    /// Klammern – "CABLE Input (VB-Audio Virtual Cable)" gehört zu
    /// "CABLE Output (VB-Audio Virtual Cable)", auch bei den A/B/C/D-Varianten.
    /// </summary>
    private static AudioDevice? FindCounterpart(VirtualCableKind kind, AudioDevice render, IReadOnlyList<AudioDevice> capture)
    {
        var driver = DriverSuffix(render.Name);
        if (driver is not null)
        {
            var paired = capture.FirstOrDefault(d =>
                string.Equals(DriverSuffix(d.Name), driver, StringComparison.OrdinalIgnoreCase) &&
                Matches(kind, d.Name, renderSide: false));
            if (paired is not null) return paired;
        }

        return capture.FirstOrDefault(d => Matches(kind, d.Name, renderSide: false));
    }

    private static string? DriverSuffix(string name)
    {
        var open = name.LastIndexOf('(');
        var close = name.LastIndexOf(')');
        return open >= 0 && close > open ? name[(open + 1)..close].Trim() : null;
    }
}
