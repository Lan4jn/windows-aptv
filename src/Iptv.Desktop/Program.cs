using System;
using Avalonia;

namespace Iptv.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => Run(args, software => BuildAvaloniaApp(software).StartWithClassicDesktopLifetime(Array.Empty<string>()));

    internal static int Run(string[] args, Func<bool, int> start)
    {
        StartupDiagnostics? diagnostics = null;
        UnhandledExceptionEventHandler? unhandled = null;
        try
        {
            var (software, log) = StartupDiagnostics.Parse(args);
            diagnostics = new StartupDiagnostics(log, software);
            StartupDiagnostics.Current = diagnostics;
            unhandled = (_, e) => { if (e.ExceptionObject is Exception exception) diagnostics.Failure(exception); Environment.ExitCode = 1; };
            AppDomain.CurrentDomain.UnhandledException += unhandled;
            diagnostics.Stage(StartupStage.Enter);
            return start(software);
        }
        catch (Exception exception)
        {
            if (diagnostics is not null) diagnostics.Failure(exception);
            else
            {
                using var fallback = new StartupDiagnostics(null, false);
                fallback.Failure(exception);
            }
            return 1;
        }
        finally
        {
            if (unhandled is not null) AppDomain.CurrentDomain.UnhandledException -= unhandled;
            StartupDiagnostics.Current = null;
            diagnostics?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(false);
    internal static AppBuilder BuildAvaloniaApp(bool software)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        if (software) builder.With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } });
        return builder;
    }
}
