using System.Collections.Concurrent;

namespace Soundbord.Services;

/// <summary>
/// Ein eigener Thread, der Aufgaben streng der Reihe nach abarbeitet.
///
/// Zweck: Tastendrücke dürfen weder im Tastatur-Hook noch im UI-Thread verarbeitet
/// werden. Der Hook wird von Windows nach ca. 300 ms zwangsweise ausgehängt, und
/// der UI-Thread darf für flüssige Darstellung nichts blockieren. Gleichzeitig muss
/// die Reihenfolge stimmen: zweimal schnell dieselbe Taste heißt an, dann aus.
/// </summary>
public sealed class SerialWorkQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;

    public SerialWorkQueue(string name)
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Post(Action work)
    {
        try { _queue.Add(work); }
        catch (Exception) { /* bereits heruntergefahren */ }
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception) { /* eine kaputte Aufgabe darf die Queue nicht töten */ }
        }
    }

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch (Exception) { }
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
