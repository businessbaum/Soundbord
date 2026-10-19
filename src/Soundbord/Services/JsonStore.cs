using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soundbord.Services;

/// <summary>
/// Laden/Speichern der JSON-Dateien. Schreibt immer über eine temporäre Datei und
/// benennt dann um – ein Absturz mitten im Speichern darf die Konfiguration nicht
/// zerschießen. Die letzte gute Fassung bleibt als .bak liegen.
/// </summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T Load<T>(string path, Func<T> fallback) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var json = File.ReadAllText(candidate);
                var value = JsonSerializer.Deserialize<T>(json, Options);
                if (value is not null) return value;
            }
            catch (Exception)
            {
                // Kaputte Datei -> nächster Kandidat, am Ende die Vorgabe.
            }
        }

        return fallback();
    }

    public static bool Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));

            if (File.Exists(path))
            {
                var backup = path + ".bak";
                File.Copy(path, backup, overwrite: true);
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Sammelt Speicheranforderungen und schreibt erst, wenn es kurz ruhig ist.
/// Ein Lautstärke-Slider löst sonst hunderte Schreibvorgänge aus.
/// </summary>
public sealed class DebouncedSaver : IDisposable
{
    private readonly Action _save;
    private readonly int _delayMs;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _pending;
    private bool _disposed;

    public DebouncedSaver(Action save, int delayMs = 700)
    {
        _save = save;
        _delayMs = delayMs;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Request()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            _timer.Change(_delayMs, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        Run();
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed || !_pending) return;
            _pending = false;
        }

        Run();
    }

    private void Run()
    {
        try { _save(); }
        catch (Exception) { /* Speichern darf die App nie umbringen */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Flush();
        _timer.Dispose();
    }
}
