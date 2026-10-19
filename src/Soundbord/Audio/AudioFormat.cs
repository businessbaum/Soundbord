using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundbord.Audio;

/// <summary>
/// Ein einziges internes Format für den gesamten Graphen: 48 kHz, Stereo, 32-Bit Float.
/// Alles wird beim Einlesen dorthin konvertiert, damit der Mixer nie umrechnen muss.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public static readonly WaveFormat Engine = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static readonly string[] SupportedExtensions =
        [".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Öffnet eine Datei als Sample-Quelle. Der Aufrufer muss <paramref name="owner"/> freigeben.</summary>
    public static ISampleProvider Open(string path, out IDisposable owner)
    {
        if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = new NAudio.Vorbis.VorbisWaveReader(path);
            owner = vorbis;
            return vorbis;
        }

        // Deckt wav/mp3/aiff nativ ab und fällt für alles andere auf Media Foundation zurück.
        var reader = new AudioFileReader(path);
        owner = reader;
        return reader;
    }

    /// <summary>Bringt eine beliebige Quelle auf 48 kHz Stereo.</summary>
    public static ISampleProvider Normalize(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2)
            source = new DownmixToStereoSampleProvider(source);

        if (source.WaveFormat.SampleRate != SampleRate)
            source = new WdlResamplingSampleProvider(source, SampleRate);

        return source;
    }

    public static ISampleProvider OpenNormalized(string path, out IDisposable owner) =>
        Normalize(Open(path, out owner));

    /// <summary>Liest nur die Länge aus dem Header, ohne die Datei zu dekodieren.</summary>
    public static TimeSpan? TryGetDuration(string path)
    {
        IDisposable? owner = null;
        try
        {
            AudioFormat.Open(path, out owner);
            return owner is WaveStream stream ? stream.TotalTime : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            owner?.Dispose();
        }
    }
}

/// <summary>
/// Mischt beliebig viele Kanäle auf Stereo herunter. NAudio bringt dafür nur
/// StereoToMono mit – 5.1-Dateien würden sonst am Mixer scheitern.
/// </summary>
public sealed class DownmixToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _buffer = [];

    public DownmixToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / 2;
        var needed = frames * _sourceChannels;
        if (_buffer.Length < needed) _buffer = new float[needed];

        var read = _source.Read(_buffer, 0, needed);
        var readFrames = read / _sourceChannels;

        for (var f = 0; f < readFrames; f++)
        {
            var baseIndex = f * _sourceChannels;
            // Kanal 0/1 tragen bei Mehrkanal-Layouts L/R; der Rest wird gleichmäßig beigemischt.
            float left = _buffer[baseIndex];
            float right = _buffer[baseIndex + 1];
            for (var c = 2; c < _sourceChannels; c++)
            {
                var extra = _buffer[baseIndex + c] * 0.7071f;
                left += extra;
                right += extra;
            }

            var scale = 1f / (1f + (_sourceChannels - 2) * 0.7071f);
            buffer[offset + f * 2] = left * scale;
            buffer[offset + f * 2 + 1] = right * scale;
        }

        return readFrames * 2;
    }
}
