using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Soundbord.Services;
using Soundbord.ViewModels;
using Soundbord.Views;

namespace Soundbord;

public partial class App : Application
{
    private AppServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = new AppServices();

            // Fenster zuerst: der Dialog-Dienst braucht ein TopLevel.
            var window = new MainWindow();
            var dialogs = new DialogService(window);
            window.DataContext = new MainWindowViewModel(_services, dialogs);

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => _services?.Dispose();
            desktop.Exit += (_, _) => _services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
