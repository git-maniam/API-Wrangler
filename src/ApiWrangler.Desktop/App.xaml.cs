using System.IO;
using System.Windows;
using ApiWrangler.Application;
using ApiWrangler.Execution;
using ApiWrangler.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiWrangler.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Do not read configuration or execute anything from the current repository at startup.
        var builder = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<IHttpTransport>(_ => new HttpTransport(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApiWrangler", "runs")));
            services.AddSingleton<HttpRequestService>();
            services.AddSingleton<IProjectRepository, ProjectRepository>();
            services.AddSingleton<LocalSettingsStore>();
            services.AddSingleton<ProjectEditorViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        });
        host = builder.Build();
        MainWindow = host.Services.GetRequiredService<MainWindow>();
        if (e.Args.Length == 2 && e.Args[0] is "--diagnostics" or "--http-smoke" or "--project-smoke" or "--dataset-smoke")
        {
            var window = (MainWindow)MainWindow;
            var output = Path.GetFullPath(e.Args[1]);
            window.ContentRendered += async (_, _) =>
            {
                try
                {
                    var model = host.Services.GetRequiredService<MainViewModel>();
                    if (e.Args[0] == "--http-smoke") await DesktopDiagnostics.TestHttpAsync(window, model, output);
                    else if (e.Args[0] == "--project-smoke") await DesktopDiagnostics.TestProjectAsync(window, model, output);
                    else if (e.Args[0] == "--dataset-smoke") await DesktopDiagnostics.TestDatasetAsync(window, model, output);
                    else await DesktopDiagnostics.RecordAsync(window, model, output);
                    Shutdown(0);
                }
                catch (Exception exception)
                {
                    Directory.CreateDirectory(output);
                    await File.WriteAllTextAsync(Path.Combine(output, "error.txt"), exception.ToString());
                    Shutdown(1);
                }
            };
        }
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        host?.Dispose();
        base.OnExit(e);
    }
}
