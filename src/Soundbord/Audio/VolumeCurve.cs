namespace Soundbord.Audio;

/// <summary>
/// Übersetzt Reglerstellungen in tatsächliche Verstärkungsfaktoren.
///
/// <para>
/// Ein Regler, der die Amplitude direkt setzt, fühlt sich falsch an: Lautstärke wird
/// logarithmisch wahrgenommen. Halbe Amplitude sind nur −6 dB und damit kaum leiser,
/// während der gesamte hörbare Bereich in die untersten Prozent gedrängt wird – man
/// hört erst knapp über Null überhaupt einen Unterschied.
/// </para>
///
/// <para>
/// Deshalb ist die Reglerstellung hier linear in <b>Dezibel</b>: 100 % entsprechen
/// 0 dB (unverändert), 0 % der Stille, und oberhalb von 100 % wird angehoben.
/// Damit ist jeder gleich große Schritt am Regler auch eine gleich große Änderung
/// im Höreindruck.
/// </para>
/// </summary>
public static class VolumeCurve
{
    /// <summary>Pegel bei Reglerstellung 0. Darunter wird komplett stumm geschaltet.</summary>
    public const double MinDecibels = -48.0;

    /// <summary>Anhebung bei doppelter Reglerstellung (200 %).</summary>
    public const double MaxBoostDecibels = 12.0;

    private const double SilenceThreshold = 0.0005;

    /// <summary>Reglerstellung (0 = stumm, 1 = unverändert, &gt;1 = angehoben) → Verstärkungsfaktor.</summary>
    public static float ToAmplitude(double position)
    {
        if (position <= SilenceThreshold) return 0f;

        var decibels = position <= 1.0
            ? MinDecibels * (1.0 - position)
            : (position - 1.0) * MaxBoostDecibels;

        return (float)Math.Pow(10.0, decibels / 20.0);
    }

    /// <summary>Umkehrung – etwa um einen alten, linear gespeicherten Wert zu übernehmen.</summary>
    public static double ToPosition(double amplitude)
    {
        if (amplitude <= SilenceThreshold) return 0.0;

        var decibels = 20.0 * Math.Log10(amplitude);

        return decibels <= 0.0
            ? Math.Clamp(1.0 - decibels / MinDecibels, 0.0, 1.0)
            : 1.0 + decibels / MaxBoostDecibels;
    }

    /// <summary>Für die Anzeige: die Reglerstellung als Dezibelwert.</summary>
    public static string Describe(double position)
    {
        if (position <= SilenceThreshold) return "stumm";

        var decibels = position <= 1.0
            ? MinDecibels * (1.0 - position)
            : (position - 1.0) * MaxBoostDecibels;

        return decibels >= 0 ? $"+{decibels:0.0} dB" : $"{decibels:0.0} dB";
    }
}
