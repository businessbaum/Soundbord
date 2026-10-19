using System.Text;
using Avalonia;
using Soundbord.Services;

namespace Soundbord;

internal static class Program
{
    // Avalonia braucht STA und darf keine SynchronizationContext-Tricks vor AppMain sehen.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception, "Task");
            e.SetObserved();
        };

        // Diagnose: SOUNDBORD_TRACE=1 schreibt Avalonias Trace-Ausgabe (u.a. fehlerhafte
        // Bindings) nach %APPDATA%\Soundbord\trace.log. Standardmäßig aus, weil eine
        // GUI-Anwendung keine Konsole hat und der Log sonst nur Platz kostet.
        if (Environment.GetEnvironmentVariable("SOUNDBORD_TRACE") == "1") EnableTrace();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, "Startup");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void EnableTrace()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var path = Path.Combine(AppPaths.DataDirectory, "trace.log");
            File.Delete(path);

            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(path));
            System.Diagnostics.Trace.AutoFlush = true;
        }
        catch (Exception)
        {
            // Ohne Trace startet die App trotzdem.
        }
    }

    private static void WriteCrashLog(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            var sb = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {source}")
                .AppendLine(ex.ToString())
                .AppendLine(new string('-', 72));
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(AppPaths.CrashLogFile, sb.ToString());
        }
        catch
        {
            // Ein fehlgeschlagenes Crash-Log darf den Crash nicht verschlimmern.
        }
    }
}
