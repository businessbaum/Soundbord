using NAudio.CoreAudioApi;

namespace Soundbord.Audio;

/// <summary>Ein Audio-Endpunkt, entkoppelt von NAudios COM-Objekten.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsCapture, bool IsSystemDefault)
{
    public const string DefaultId = "__default__";

    public static AudioDevice SystemDefaultRender { get; } =
        new(DefaultId, "Systemstandard", false, true);

    public static AudioDevice SystemDefaultCapture { get; } =
        new(DefaultId, "Systemstandard", true, true);
}

public static class AudioDevices
{
    public static List<AudioDevice> Render() => Enumerate(DataFlow.Render);

    public static List<AudioDevice> Capture() => Enumerate(DataFlow.Capture);

    private static List<AudioDevice> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception)
            {
                // Kein Standardgerät vorhanden (z.B. gar keine Soundkarte) – kein Fehlerfall.
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new AudioDevice(
                        device.ID,
                        SafeName(device),
                        flow == DataFlow.Capture,
                        device.ID == defaultId));
                }
            }
        }
        catch (Exception)
        {
            // Bei Problemen mit dem Audio-Stack lieber eine leere Liste als ein Absturz.
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch (Exception) { return device.ID; }
    }

    /// <summary>
    /// Holt das MMDevice zu einer ID. Fällt auf den Systemstandard zurück, wenn die
    /// ID leer ist oder das Gerät inzwischen verschwunden ist (USB-Headset abgezogen).
    /// </summary>
    public static MMDevice? Resolve(string? id, bool capture)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            var flow = capture ? DataFlow.Capture : DataFlow.Render;

            if (!string.IsNullOrWhiteSpace(id) && id != AudioDevice.DefaultId)
            {
                try
                {
                    var device = enumerator.GetDevice(id);
                    if (device.State == DeviceState.Active) return device;
                    device.Dispose();
                }
                catch (Exception)
                {
                    // Gerät weg -> Standard nehmen.
                }
            }

            try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
            catch (Exception) { return null; }
        }
        finally
        {
            enumerator.Dispose();
        }
    }
}
