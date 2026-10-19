namespace Soundbord.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Soundbord");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string LibraryFile { get; } = Path.Combine(DataDirectory, "sounds.json");

    public static string CrashLogFile { get; } = Path.Combine(DataDirectory, "crash.log");
}
