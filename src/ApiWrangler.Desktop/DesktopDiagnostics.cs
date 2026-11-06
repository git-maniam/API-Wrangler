using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ApiWrangler.Desktop;

internal static class DesktopDiagnostics
{
    public static async Task TestHttpAsync(MainWindow window, MainViewModel model, string directory)
    {
        Directory.CreateDirectory(directory);
        window.RequestTabs.SelectedIndex = 1;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        const string body = "{\"message\":\"desktop binding verified\"}";
        window.RequestBodyEditor.Text = body;
        if (model.RequestBody != body) throw new InvalidOperationException("Editor two-way binding did not update the request.");
        model.Method = "POST";
        model.Url = "http://127.0.0.1:5057/echo";
        await model.SendCommand.ExecuteAsync(null);
        if (!model.Status.StartsWith("Passed", StringComparison.Ordinal)) throw new InvalidOperationException("Desktop HTTP request failed.");
        using var response = JsonDocument.Parse(model.ResponseBody);
        if (response.RootElement.GetProperty("body").GetString() != body) throw new InvalidOperationException("HTTP body did not round trip.");
        Capture(window, Path.Combine(directory, "http-response.png"));
        var ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => ticks++;
        model.Method = "GET";
        model.RequestBody = "";
        model.Url = "http://127.0.0.1:5057/delay";
        timer.Start();
        try
        {
            var send = model.SendCommand.ExecuteAsync(null);
            await Task.Delay(300);
            model.SendCommand.Cancel();
            await send;
            if (!model.Status.StartsWith("Cancelled", StringComparison.Ordinal) || ticks == 0)
                throw new InvalidOperationException("Cancellation or dispatcher responsiveness failed.");
        }
        finally { timer.Stop(); }
        await File.WriteAllTextAsync(Path.Combine(directory, "http-smoke.json"), JsonSerializer.Serialize(new
        { editorRoundTrip = true, httpRoundTrip = true, cancellation = true, dispatcherTicksDuringRequest = ticks }));
    }

    public static async Task RecordAsync(MainWindow window, MainViewModel model, string directory)
    {
        var startupMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        Directory.CreateDirectory(directory);
        await Task.Delay(500);
        var privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
        Capture(window, Path.Combine(directory, "workbench.png"));
        var watch = Stopwatch.StartNew();
        window.MainTabs.SelectedIndex = 1;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var datasetMs = watch.Elapsed.TotalMilliseconds;
        var realizedRows = CountRows(window.FixtureGrid);
        if (realizedRows is < 1 or > 200) throw new InvalidOperationException("Dataset row virtualization failed.");
        Capture(window, Path.Combine(directory, "dataset.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "metrics.json"), JsonSerializer.Serialize(new
        {
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            logicalCores = Environment.ProcessorCount,
            startupMs,
            privateBytes,
            datasetMs,
            realizedRows,
            cachedRows = model.Dataset.CachedRowCount,
            datasetCount = model.Dataset.Count,
            note = "Single process-start sample, not a cold-start guarantee or ordinary-project memory acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int CountRows(DependencyObject parent)
    {
        var count = parent is DataGridRow ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) count += CountRows(VisualTreeHelper.GetChild(parent, i));
        return count;
    }

    private static void Capture(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
