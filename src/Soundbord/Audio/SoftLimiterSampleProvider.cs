using NAudio.Wave;

namespace Soundbord.Audio;

/// <summary>
/// Fängt Übersteuerung ab, bevor sie das Ausgabegerät erreicht.
/// </summary>
/// <remarks>
/// <para>
/// Die Summe aus mehreren gleichzeitigen Sounds, angehobenen Einzelpegeln und dem
/// Mikrofon kann den Bereich von ±1,0 verlassen. WASAPI schneidet alles darüber
/// hart ab – hörbar als Knistern und Verzerrung, und zwar genau in den lautesten
/// Momenten.
/// </para>
/// <para>
/// Statt hart abzuschneiden wird oberhalb einer Schwelle weich zusammengedrückt:
/// Unterhalb bleibt das Signal <b>vollkommen unangetastet</b>, darüber nähert es
/// sich über eine Tangens-hyperbolicus-Kennlinie asymptotisch 1,0 und erreicht ihn
/// nie. Das ist eine reine Kennlinie ohne Nachregelung – damit gibt es kein Pumpen
/// und keine Verzögerung, was einen echten Limiter mit Vorausschau erfordern würde.
/// </para>
/// </remarks>
public sealed class SoftLimiterSampleProvider : ISampleProvider
{
    /// <summary>Ab hier wird zusammengedrückt. Darunter passiert nichts.</summary>
    private const float Threshold = 0.80f;

    private const float Range = 1f - Threshold;

    private readonly ISampleProvider _source;
    private int _engaged;

    public SoftLimiterSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Hat der Schutz seit der letzten Abfrage eingegriffen? Setzt sich beim Lesen
    /// zurück, damit die Anzeige nicht dauerhaft hängen bleibt.
    /// </summary>
    public bool ConsumeEngaged() => Interlocked.Exchange(ref _engaged, 0) != 0;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (!Enabled || read <= 0) return read;

        var engaged = false;

        for (var i = offset; i < offset + read; i++)
        {
            var sample = buffer[i];
            var magnitude = Math.Abs(sample);
            if (magnitude <= Threshold) continue;

            var excess = (magnitude - Threshold) / Range;
            buffer[i] = MathF.CopySign(Threshold + Range * MathF.Tanh(excess), sample);
            engaged = true;
        }

        if (engaged) Interlocked.Exchange(ref _engaged, 1);
        return read;
    }
}
