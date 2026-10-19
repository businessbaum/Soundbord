using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundbord.Audio;

/// <summary>Auf welchem Weg der Desktop-Ton abgegriffen wird.</summary>
public enum DesktopCaptureKind
{
    /// <summary>
    /// Prozess-Loopback: der gesamte Systemton <b>außer</b> Soundbord selbst.
    /// Der saubere Weg – die eigenen Sounds kommen dadurch nicht doppelt an.
    /// </summary>
    ExcludingSelf,

    /// <summary>
    /// Prozess-Loopback auf genau eine Anwendung samt ihrer Unterprozesse.
    /// Alles andere auf dem Rechner bleibt draußen.
    /// </summary>
    SingleApp,

    /// <summary>
    /// Geräte-Loopback: alles, was auf dem Wiedergabegerät ankommt – die eigenen
    /// Sounds eingeschlossen. Nur als Rückfallebene.
    /// </summary>
    WholeDevice,
}

/// <summary>Was abgegriffen werden soll.</summary>
/// <param name="ProcessId">
/// Die Anwendung, deren Ton übertragen wird – samt ihrer Unterprozesse.
/// <c>null</c> heißt: alles außer Soundbord selbst.
/// </param>
public sealed record DesktopCaptureTarget(int? ProcessId, string Name)
{
    public static DesktopCaptureTarget WholeDesktop { get; } = new(null, "Ganzer Desktop");
}

/// <summary>
/// Greift den laufenden Desktop-Ton ab, damit er in das virtuelle Kabel gemischt
/// werden kann. Für den Zuhörer in Discord klingt es, als käme Musik oder Spielton
/// aus dem Mikrofon.
/// </summary>
/// <remarks>
/// <para>
/// Der naheliegende Weg – <c>WasapiLoopbackCapture</c> auf den Kopfhörern – hat einen
/// Haken: dort landet auch die eigene Ausgabe. Jeder Soundbord-Sound käme dann zweimal
/// im Kabel an, einmal direkt und einmal um die Loopback-Latenz versetzt. Das Ergebnis
/// ist ein hörbares Echo bzw. ein Kammfilter.
/// </para>
/// <para>
/// Windows 10 (Build 20348) und Windows 11 lösen das mit dem <b>Prozess-Loopback</b>:
/// <c>ActivateAudioInterfaceAsync</c> auf dem Pseudo-Gerät <c>VAD\Process_Loopback</c>,
/// mit <c>EXCLUDE_TARGET_PROCESS_TREE</c> auf die eigene Prozess-ID. Damit liefert
/// Windows alles, was <i>andere</i> Programme abspielen – Browser, Spotify, Spiel –
/// und lässt Soundbord aus. Genau das wollen wir.
/// </para>
/// <para>
/// Die Schnittstelle ist dokumentiert, aber nicht in NAudio abgebildet, deshalb steht
/// das COM-Interop hier von Hand. Schlägt die Aktivierung fehl, fällt die Klasse auf
/// das Geräte-Loopback zurück und meldet das über <see cref="Kind"/>.
/// </para>
/// </remarks>
public sealed class DesktopCapture : IDisposable
{
    private readonly ProcessLoopbackCapture? _process;
    private readonly WasapiLoopbackCapture? _device;

    private DesktopCapture(ProcessLoopbackCapture process, DesktopCaptureTarget target)
    {
        _process = process;
        Kind = target.ProcessId is null ? DesktopCaptureKind.ExcludingSelf : DesktopCaptureKind.SingleApp;
        WaveFormat = process.WaveFormat;
        SourceName = target.Name;
        process.DataAvailable += (_, e) => DataAvailable?.Invoke(this, e);
    }

    private DesktopCapture(WasapiLoopbackCapture device, string deviceName)
    {
        _device = device;
        Kind = DesktopCaptureKind.WholeDevice;
        WaveFormat = device.WaveFormat;
        SourceName = deviceName;
        device.DataAvailable += (_, e) => DataAvailable?.Invoke(this, e);
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public WaveFormat WaveFormat { get; }

    public DesktopCaptureKind Kind { get; }

    /// <summary>Was abgegriffen wird – für die Anzeige.</summary>
    public string SourceName { get; }

    /// <summary>
    /// Öffnet den Abgriff. Versucht zuerst das Prozess-Loopback und fällt bei
    /// Misserfolg auf das Geräte-Loopback zurück.
    /// </summary>
    /// <param name="target">Einzelne Anwendung oder der ganze Desktop.</param>
    /// <param name="fallbackDeviceId">Gerät für die Rückfallebene (i.d.R. die Kopfhörer).</param>
    /// <param name="note">Hinweis für den Nutzer, wenn nur die Rückfallebene ging.</param>
    public static DesktopCapture Open(DesktopCaptureTarget target, string? fallbackDeviceId, out string? note)
    {
        note = null;

        try
        {
            return new DesktopCapture(new ProcessLoopbackCapture(AudioFormat.Engine, target.ProcessId), target);
        }
        catch (Exception ex)
        {
            note = ex.Message;
        }

        // Auf eine einzelne Anwendung lässt sich nur per Prozess-Loopback abgreifen.
        // Die Rückfallebene kennt nur ganze Geräte und wäre hier schlicht falsch.
        if (target.ProcessId is not null)
            throw new InvalidOperationException(note);

        // Rückfallebene: das ganze Gerät mitschneiden. Funktioniert überall, nimmt
        // aber die eigene Ausgabe mit auf.
        var device = AudioDevices.Resolve(fallbackDeviceId, capture: false)
                     ?? throw new InvalidOperationException("Kein Wiedergabegerät für den Mitschnitt gefunden.");

        try
        {
            var capture = new WasapiLoopbackCapture(device);
            capture.StartRecording();
            note = $"Prozess-Loopback nicht verfügbar ({note}). Es wird „{device.FriendlyName}“ " +
                   "komplett mitgeschnitten – die eigenen Soundbord-Sounds kommen dadurch doppelt an.";
            return new DesktopCapture(capture, device.FriendlyName);
        }
        catch (Exception)
        {
            device.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _process?.Dispose();

        if (_device is not null)
        {
            try { _device.StopRecording(); } catch (Exception) { }
            try { _device.Dispose(); } catch (Exception) { }
        }
    }
}

/// <summary>
/// WASAPI-Prozess-Loopback. Nimmt entweder den Ton genau einer Anwendung auf oder den
/// aller Anwendungen außer der eigenen – dieselbe Schnittstelle, nur ein anderer Modus.
/// <para>
/// Läuft vollständig auf einem eigenen MTA-Thread: die Aktivierung ruft ihren
/// Abschluss-Handler aus einem fremden Thread auf, und ohne MTA würde COM den Aufruf
/// in unsere Nachrichtenschleife marshallen, auf die wir gerade warten. Das wäre ein
/// Deadlock.
/// </para>
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualDevicePath = "VAD\\Process_Loopback";

    private const int ShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int StreamFlagsEventCallback = 0x00040000;
    private const int BufferFlagsSilent = 0x1;

    /// <summary>Puffergröße des Aufnahmeclients. Reine Reserve, keine Latenz.</summary>
    private const long BufferDuration = 200 * 10_000L;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly WaveFormat _format;
    private readonly int? _targetProcessId;

    private volatile bool _stop;
    private Exception? _startError;
    private byte[] _scratch = [];

    /// <param name="targetProcessId">
    /// Prozessbaum, dessen Ton übertragen wird. <c>null</c> kehrt die Auswahl um:
    /// dann wird alles übertragen <b>außer</b> dem eigenen Prozessbaum.
    /// </param>
    public ProcessLoopbackCapture(WaveFormat format, int? targetProcessId)
    {
        _format = format;
        _targetProcessId = targetProcessId;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Soundbord Desktop-Loopback",
            // Etwas über Normal: setzt der Thread aus, entstehen Löcher im Ton.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _stop = true;
            throw new TimeoutException("Windows hat den Systemton-Abgriff nicht rechtzeitig bereitgestellt.");
        }

        if (_startError is not null)
        {
            _stop = true;
            throw _startError;
        }
    }

    public WaveFormat WaveFormat => _format;

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public void Dispose()
    {
        _stop = true;
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _ready.Dispose();
    }

    // ================= Der Aufnahme-Thread =================

    private void Run()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        AutoResetEvent? pulse = null;

        try
        {
            client = Activate(_targetProcessId);

            var sessionGuid = Guid.Empty;
            var hr = client.Initialize(
                ShareModeShared,
                StreamFlagsLoopback | StreamFlagsEventCallback,
                BufferDuration,
                0,
                _format,
                ref sessionGuid);

            if (hr < 0) throw new COMException($"Der Systemton-Abgriff lehnte das Format ab (0x{hr:X8}).", hr);

            pulse = new AutoResetEvent(false);
            Check(client.SetEventHandle(pulse.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Check(client.GetService(IidAudioCaptureClient, out var service), "GetService");

            capture = (IAudioCaptureClient)service;
            Check(client.Start(), "Start");
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            Release(ref capture, ref client, pulse);
            return;
        }

        _ready.Set();

        try
        {
            while (!_stop)
            {
                // Kurze Wartezeit statt unendlich: liefert kein Programm gerade Ton,
                // bleibt das Ereignis aus, und wir müssen trotzdem auf Stop reagieren.
                pulse!.WaitOne(100);
                Drain(capture!);
            }
        }
        catch (Exception)
        {
            // Gerätewechsel im laufenden Betrieb – der Abgriff endet still.
        }
        finally
        {
            try { client!.Stop(); } catch (Exception) { }
            Release(ref capture, ref client, pulse);
        }
    }

    /// <summary>Holt alle bereitliegenden Pakete ab. Ein Ereignis kann mehrere bedeuten.</summary>
    private void Drain(IAudioCaptureClient capture)
    {
        var blockAlign = _format.BlockAlign;

        while (!_stop)
        {
            if (capture.GetNextPacketSize(out var pending) < 0 || pending == 0) return;
            if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) < 0) return;

            try
            {
                var bytes = (int)frames * blockAlign;
                if (bytes <= 0) continue;

                if (_scratch.Length < bytes) _scratch = new byte[bytes];

                // Meldet Windows Stille, ist der Zeiger nicht zwingend gültig.
                if ((flags & BufferFlagsSilent) != 0) Array.Clear(_scratch, 0, bytes);
                else Marshal.Copy(data, _scratch, 0, bytes);

                DataAvailable?.Invoke(this, new WaveInEventArgs(_scratch, bytes));
            }
            finally
            {
                capture.ReleaseBuffer(frames);
            }
        }
    }

    /// <summary>
    /// Aktiviert einen <c>IAudioClient</c> auf dem Pseudo-Gerät für Prozess-Loopback.
    /// Die Aktivierung ist asynchron; hier wird sie auf ein synchrones Ergebnis
    /// heruntergebrochen, weil der Thread ohnehin nur dafür da ist.
    /// </summary>
    private static IAudioClient Activate(int? targetProcessId)
    {
        // Zwei Modi derselben Schnittstelle: entweder genau diesen Prozessbaum
        // aufnehmen – oder genau diesen aussparen und alles andere aufnehmen.
        var parameters = new ActivationParams
        {
            ActivationType = 1,                          // PROCESS_LOOPBACK
            TargetProcessId = (uint)(targetProcessId ?? Environment.ProcessId),
            LoopbackMode = targetProcessId is null
                ? 1                                      // EXCLUDE_TARGET_PROCESS_TREE
                : 0,                                     // INCLUDE_TARGET_PROCESS_TREE
        };

        var blob = IntPtr.Zero;
        var variant = IntPtr.Zero;
        try
        {
            var size = Marshal.SizeOf<ActivationParams>();
            blob = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(parameters, blob, false);

            variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            Marshal.StructureToPtr(
                new PropVariantBlob { VarType = 0x41 /* VT_BLOB */, Size = size, Data = blob },
                variant,
                false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevicePath, IidAudioClient, variant, handler, out var operation);

            if (!handler.Done.Wait(TimeSpan.FromSeconds(4)))
                throw new TimeoutException("Windows hat den Systemton-Abgriff nicht bestätigt.");

            var hr = operation.GetActivateResult(out var activateResult, out var activated);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            if (activateResult < 0)
            {
                // Häufigster Fall: das System ist älter als Windows 10 Build 20348.
                throw new COMException(
                    $"Der Systemton-Abgriff steht nicht zur Verfügung (0x{activateResult:X8}). " +
                    "Er setzt Windows 10 ab Build 20348 oder Windows 11 voraus.",
                    activateResult);
            }

            return (IAudioClient)activated;
        }
        finally
        {
            if (variant != IntPtr.Zero) Marshal.FreeHGlobal(variant);
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"Systemton-Abgriff: {what} schlug fehl (0x{hr:X8}).", hr);
    }

    private static void Release(ref IAudioCaptureClient? capture, ref IAudioClient? client, AutoResetEvent? pulse)
    {
        if (capture is not null)
        {
            try { Marshal.ReleaseComObject(capture); } catch (Exception) { }
            capture = null;
        }

        if (client is not null)
        {
            try { Marshal.ReleaseComObject(client); } catch (Exception) { }
            client = null;
        }

        pulse?.Dispose();
    }

    // ================= COM-Interop =================

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS – die Union ist hier auf ihren einzigen Fall reduziert.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    /// <summary>PROPVARIANT, auf die BLOB-Variante zugeschnitten.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public int Size;
        public IntPtr Data;
    }

    /// <summary>
    /// Marker ohne Methoden. Signalisiert COM, dass das Objekt threadübergreifend
    /// direkt aufgerufen werden darf – sonst versucht COM zu marshallen.
    /// </summary>
    [ComImport]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Done.Set();
            return 0;
        }
    }

    /// <remarks>Die Reihenfolge der Methoden bildet die Sprungtabelle ab und ist bindend.</remarks>
    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            [In] WaveFormat format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, [In] WaveFormat format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
