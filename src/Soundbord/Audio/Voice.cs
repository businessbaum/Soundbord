using NAudio.Wave;

namespace Soundbord.Audio;

/// <summary>
/// Eine laufende Stimme im Mixer: Lautstärke, kurze Ein-/Ausblende gegen Knackser
/// und ein Stopp, der den Mixer sauber verlassen lässt.
/// </summary>
/// <remarks>
/// <see cref="MixingSampleProvider"/> entfernt eine Quelle, sobald sie weniger
/// Samples liefert als angefordert. Genau das nutzen wir zum Aufräumen – es
/// braucht dafür keinerlei Sperre im Audio-Thread.
/// </remarks>
internal sealed class Voice : ISampleProvider
{
    private const int FadeInFrames = 144;   // 3 ms
    private const int FadeOutFrames = 384;  // 8 ms
    private const int PauseFadeFrames = 240; // 5 ms, gegen Knackser beim Pausieren

    private readonly ISampleProvider _source;
    private readonly IDisposable? _owner;
    private readonly IPositionedSource? _positioned;

    private int _fadeInPos;
    private int _fadeOutPos = -1;
    private int _pauseFade;             // 0 = voll hörbar, PauseFadeFrames = stumm
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private bool _finished;

    public Voice(ISampleProvider source, IDisposable? owner, float volume)
    {
        _source = source;
        _owner = owner;
        _positioned = source as IPositionedSource;
        Volume = volume;
    }

    public WaveFormat WaveFormat => AudioFormat.Engine;

    public float Volume { get; set; }

    public bool IsPaused => _paused;

    public TimeSpan Position => _positioned?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => _positioned?.Duration ?? TimeSpan.Zero;

    /// <summary>Wird im Audio-Thread ausgelöst – Empfänger müssen selbst marshallen.</summary>
    public event EventHandler? Finished;

    public void Stop() => _stopRequested = true;

    public void SetPaused(bool paused) => _paused = paused;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_finished) return 0;

        if (_stopRequested && _fadeOutPos < 0) _fadeOutPos = 0;

        // Pausiert: Stille liefern, aber die volle Länge zurückgeben, damit der
        // Mixer die Stimme behält. Die Quelle wird dabei nicht weitergelesen –
        // die Position bleibt also stehen.
        if (_paused && _fadeOutPos < 0 && _pauseFade >= PauseFadeFrames)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        var read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            Finish();
            return 0;
        }

        var frames = read / AudioFormat.Channels;
        var volume = Volume;

        for (var f = 0; f < frames; f++)
        {
            var gain = volume;

            if (_fadeInPos < FadeInFrames)
            {
                gain *= (float)_fadeInPos / FadeInFrames;
                _fadeInPos++;
            }

            // Weiche Rampe in die Pause hinein und wieder heraus.
            if (_paused && _pauseFade < PauseFadeFrames) _pauseFade++;
            else if (!_paused && _pauseFade > 0) _pauseFade--;

            if (_pauseFade > 0) gain *= 1f - (float)_pauseFade / PauseFadeFrames;

            if (_fadeOutPos >= 0)
            {
                if (_fadeOutPos >= FadeOutFrames)
                {
                    // Ausgeblendet: Rest verwerfen und als Teilblock zurückgeben,
                    // damit der Mixer uns entfernt.
                    var produced = f * AudioFormat.Channels;
                    Finish();
                    return produced;
                }

                gain *= 1f - (float)_fadeOutPos / FadeOutFrames;
                _fadeOutPos++;
            }

            var i = offset + f * AudioFormat.Channels;
            buffer[i] *= gain;
            buffer[i + 1] *= gain;
        }

        if (read < count) Finish();
        return read;
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;

        try { _owner?.Dispose(); }
        catch (Exception) { /* Datei-Handle war schon weg – irrelevant */ }

        Finished?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Eine Quelle, die Auskunft über ihre Abspielposition geben kann.</summary>
internal interface IPositionedSource : ISampleProvider
{
    TimeSpan Position { get; }

    /// <summary><see cref="TimeSpan.Zero"/>, wenn die Länge nicht bekannt ist.</summary>
    TimeSpan Duration { get; }
}

/// <summary>Spielt aus dem RAM-Cache. Kein I/O, keine Allokation im Audio-Thread.</summary>
internal sealed class CachedSoundSource : IPositionedSource
{
    private readonly CachedSound _sound;
    private readonly bool _loop;
    private int _position;

    public CachedSoundSource(CachedSound sound, bool loop)
    {
        _sound = sound;
        _loop = loop;
    }

    public WaveFormat WaveFormat => AudioFormat.Engine;

    public TimeSpan Position => TimeSpan.FromSeconds(
        (double)Volatile.Read(ref _position) / AudioFormat.Channels / AudioFormat.SampleRate);

    public TimeSpan Duration => _sound.Duration;

    public int Read(float[] buffer, int offset, int count)
    {
        var data = _sound.Data;
        if (data.Length == 0) return 0;

        var total = 0;
        while (total < count)
        {
            var available = data.Length - _position;
            if (available <= 0)
            {
                if (!_loop) break;
                _position = 0;
                continue;
            }

            var n = Math.Min(count - total, available);
            Array.Copy(data, _position, buffer, offset + total, n);
            _position += n;
            total += n;
        }

        return total;
    }
}

/// <summary>
/// Streamt lange Dateien von der Platte, statt sie komplett in den RAM zu ziehen.
/// </summary>
internal sealed class StreamingSource : IPositionedSource, IDisposable
{
    private readonly bool _loop;
    private readonly string _path;
    private IDisposable? _owner;
    private ISampleProvider? _chain;
    private bool _disposed;

    public StreamingSource(string path, bool loop)
    {
        _path = path;
        _loop = loop;
        _chain = AudioFormat.OpenNormalized(path, out var owner);
        _owner = owner;
    }

    public WaveFormat WaveFormat => AudioFormat.Engine;

    // Position und Länge stammen direkt vom Dateileser. Durch die Puffer der
    // Umsamplung eilt der Wert der Ausgabe um wenige Millisekunden voraus –
    // für eine Fortschrittsanzeige belanglos.
    public TimeSpan Position => SafeTime(s => s.CurrentTime);

    public TimeSpan Duration => SafeTime(s => s.TotalTime);

    private TimeSpan SafeTime(Func<WaveStream, TimeSpan> read)
    {
        try { return _owner is WaveStream stream ? read(stream) : TimeSpan.Zero; }
        catch (Exception) { return TimeSpan.Zero; }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed || _chain is null) return 0;

        var total = 0;
        while (total < count)
        {
            var read = _chain.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                if (!_loop || !Restart()) break;
                continue;
            }

            total += read;
        }

        return total;
    }

    private bool Restart()
    {
        try
        {
            _owner?.Dispose();
            _chain = AudioFormat.OpenNormalized(_path, out var owner);
            _owner = owner;
            return true;
        }
        catch (Exception)
        {
            _chain = null;
            _owner = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _owner?.Dispose(); } catch (Exception) { /* egal */ }
        _owner = null;
        _chain = null;
    }
}
