using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace HbkWwise.Gui;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startupProject = Environment.GetCommandLineArgs()
                .Skip(1)
                .FirstOrDefault(path => Path.GetExtension(path)
                    .Equals(".hbkproj", StringComparison.OrdinalIgnoreCase));
            var window = new MainWindow(startupProject);

            desktop.MainWindow = window;
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                System.Diagnostics.Trace.TraceError(args.Exception.ToString());
                window.ReportError(args.Exception.GetBaseException().Message);
                args.Handled = true;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
