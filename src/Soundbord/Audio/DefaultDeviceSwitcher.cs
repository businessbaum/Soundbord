using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Soundbord.Audio;

/// <summary>
/// Setzt das Standard-Aufnahmegerät von Windows.
/// </summary>
/// <remarks>
/// <para>
/// Warum das nötig ist: Discord und viele andere Programme greifen fest auf das
/// Windows-Standardmikrofon zu, ohne eigene Geräteauswahl anzubieten. Wer zwischen
/// Headset und Kabel wechseln will, müsste sonst jedes Mal in die Windows-Einstellungen.
/// </para>
/// <para>
/// Warum es so umständlich ist: Windows bietet dafür <b>keine</b> dokumentierte
/// Schnittstelle an. Der einzige Weg führt über <c>IPolicyConfig</c> – eine
/// undokumentierte COM-Schnittstelle, auf der auch verbreitete Werkzeuge wie
/// SoundSwitch und AudioSwitcher aufsetzen. Sie ist seit Windows 7 stabil, aber
/// eben nicht zugesichert; deshalb ist hier alles abgesichert und ein Fehlschlag
/// wird gemeldet statt geworfen.
/// </para>
/// <para>
/// Die Reihenfolge der Methoden im Interface ist <b>bindend</b>: COM adressiert sie
/// über ihre Position in der Sprungtabelle, nicht über den Namen. Die zehn Methoden
/// vor <c>SetDefaultEndpoint</c> werden nie aufgerufen und dienen nur dazu, die
/// Position korrekt zu halten.
/// </para>
/// </remarks>
public static class DefaultDeviceSwitcher
{
    private static readonly Guid PolicyConfigClientClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    /// <summary>Alle drei Rollen setzen, damit Windows-Einstellungen und Programme übereinstimmen.</summary>
    private static readonly int[] AllRoles = [0 /* Console */, 1 /* Multimedia */, 2 /* Communications */];

    /// <summary>Ist der Umschalter auf diesem System überhaupt verfügbar?</summary>
    public static bool IsSupported
    {
        get
        {
            try { return Type.GetTypeFromCLSID(PolicyConfigClientClsid) is not null; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Setzt das Standard-Aufnahmegerät. Gibt bei Misserfolg eine Meldung zurück.</summary>
    public static bool TrySetDefaultCaptureDevice(string deviceId, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == AudioDevice.DefaultId)
        {
            error = "Kein gültiges Gerät ausgewählt.";
            return false;
        }

        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(PolicyConfigClientClsid);
            if (type is null)
            {
                error = "Windows stellt die nötige Schnittstelle nicht bereit.";
                return false;
            }

            instance = Activator.CreateInstance(type);
            if (instance is not IPolicyConfig config)
            {
                error = "Die Schnittstelle konnte nicht angesprochen werden.";
                return false;
            }

            foreach (var role in AllRoles)
            {
                var hr = config.SetDefaultEndpoint(deviceId, role);
                if (hr < 0)
                {
                    error = $"Windows hat die Umstellung abgelehnt (0x{hr:X8}).";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (instance is not null)
            {
                try { Marshal.ReleaseComObject(instance); } catch (Exception) { }
            }
        }
    }

    /// <summary>Die ID des aktuellen Standard-Aufnahmegeräts, oder null.</summary>
    public static string? GetDefaultCaptureDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // --- Platzhalter: nur für die richtige Position in der Sprungtabelle ---
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, out IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);

        // --- die eigentlich gesuchte Methode (Position 11) ---
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
