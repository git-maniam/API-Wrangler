using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApiWrangler.Storage;

namespace ApiWrangler.Desktop;

internal static class DesktopDiagnostics
{
    public static async Task TestProjectAsync(MainWindow window, MainViewModel model, string directory)
    {
        Directory.CreateDirectory(directory);
        await ProjectEditorRegressions.RunAsync(directory);
        var project = Path.Combine(directory, "project-" + Guid.NewGuid().ToString("N"));
        await using (var initialized = await new ProjectRepository(Path.Combine(directory, "init-state")).InitializeAsync(project, "Desktop smoke")) { }
        var editor = model.ProjectEditor;
        editor.ProjectPath = project;
        await editor.OpenProjectCommand.ExecuteAsync(null);
        await using (var second = new ProjectEditorViewModel(new ProjectRepository()))
        {
            second.ProjectPath = project;
            await second.OpenProjectCommand.ExecuteAsync(null);
            if (second.CanEdit || second.CanUseDisk || second.CanUseDraft || !second.IsEditorReadOnly ||
                !second.ProjectStatus.Contains("read-only", StringComparison.Ordinal))
                throw new InvalidOperationException("Second desktop project editor did not open read-only.");
        }
        var futureProject = Path.Combine(directory, "future-" + Guid.NewGuid().ToString("N"));
        await using (var initialized = await new ProjectRepository(Path.Combine(directory, "init-state")).InitializeAsync(futureProject, "Future schema")) { }
        var futureRequest = Path.Combine(futureProject, "collections/default/requests/hello.request.json");
        var futureContent = JsonNode.Parse(await File.ReadAllTextAsync(futureRequest))!.AsObject();
        futureContent["schemaVersion"] = 2;
        await File.WriteAllTextAsync(futureRequest, futureContent.ToJsonString());
        await using (var future = new ProjectEditorViewModel(new ProjectRepository()))
        {
            future.ProjectPath = futureProject;
            await future.OpenProjectCommand.ExecuteAsync(null);
            if (future.CanEdit || !future.IsEditorReadOnly || future.Assets.Count == 0)
                throw new InvalidOperationException("Future-schema project was not inspectable and read-only in the desktop.");
        }
        var shutdownProject = Path.Combine(directory, "shutdown-" + Guid.NewGuid().ToString("N"));
        await using (var initialized = await new ProjectRepository(Path.Combine(directory, "init-state")).InitializeAsync(shutdownProject, "Shutdown draft")) { }
        var shutdownEditor = new ProjectEditorViewModel(new ProjectRepository());
        shutdownEditor.ProjectPath = shutdownProject;
        await shutdownEditor.OpenProjectCommand.ExecuteAsync(null);
        var shutdownAsset = shutdownEditor.Assets.First(a => a.Kind == ApiWrangler.Domain.AssetKind.Request);
        shutdownEditor.SelectedAsset = shutdownAsset;
        await Task.Delay(100);
        shutdownEditor.EditorText = "{ unfinished";
        await shutdownEditor.DisposeAsync();
        await using (var reader = await new ProjectRepository().OpenAsync(shutdownProject, writable: false))
        {
            if ((await reader.ReadDraftAsync(shutdownAsset.Path))?.Text != "{ unfinished")
                throw new InvalidOperationException("Immediate shutdown lost the unfinished editor buffer.");
        }
        window.MainTabs.SelectedIndex = 0;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(directory, "project-editor.png"));
        foreach (var control in new FrameworkElement[] { window.ProjectPathBox, window.ProjectAssetSelector,
            window.ProjectDraftEditor, window.ProjectDiskEditor, window.ProjectStatusText })
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(control);
            if (peer is null || string.IsNullOrWhiteSpace(peer.GetName()))
                throw new InvalidOperationException($"Project control {control.Name} has no accessible name.");
        }
        if (FrameworkElementAutomationPeer.FromElement(window.ProjectStatusText)?.GetName() != editor.ProjectStatus ||
            AutomationProperties.GetLiveSetting(window.ProjectStatusText) != AutomationLiveSetting.Polite)
            throw new InvalidOperationException("Project status is not exposed as a polite live message.");
        var draftPeer = FrameworkElementAutomationPeer.FromElement(window.ProjectDraftEditor);
        if (draftPeer?.GetPattern(PatternInterface.Value) is not IValueProvider draftValue ||
            draftValue.Value != window.ProjectDraftEditor.Text || draftValue.IsReadOnly)
            throw new InvalidOperationException("The editable project draft is not available to UI Automation as a value.");
        var diskPeer = FrameworkElementAutomationPeer.FromElement(window.ProjectDiskEditor);
        if (diskPeer?.GetPattern(PatternInterface.Value) is not IValueProvider diskValue ||
            diskValue.Value != window.ProjectDiskEditor.Text || !diskValue.IsReadOnly)
            throw new InvalidOperationException("The read-only disk comparison is not available to UI Automation as a value.");
        var asset = editor.Assets.First(a => a.Kind == ApiWrangler.Domain.AssetKind.Request);
        editor.SelectedAsset = asset;
        await Task.Delay(100);
        var draft = JsonNode.Parse(editor.EditorText)!.AsObject();
        draft["name"] = "Local draft";
        editor.EditorText = draft.ToJsonString();
        var disk = (JsonObject)asset.Content.DeepClone();
        disk["name"] = "External edit";
        var file = Path.Combine(project, asset.Path.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(file, disk.ToJsonString());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!editor.HasConflict && DateTime.UtcNow < deadline) await Task.Delay(100);
        if (!editor.HasConflict || !editor.CanUseDisk || !editor.CanUseDraft ||
            !editor.EditorText.Contains("Local draft", StringComparison.Ordinal) ||
            !editor.DiskText.Contains("External edit", StringComparison.Ordinal))
            throw new InvalidOperationException("Dirty project draft or external edit conflict was lost.");
        await editor.SaveAssetCommand.ExecuteAsync(null);
        if (!editor.HasConflict) throw new InvalidOperationException("Conflicted draft saved without a decision.");
        await editor.UseDraftCommand.ExecuteAsync(null);
        if (editor.CanUseDraft || !editor.CanUseDisk)
            throw new InvalidOperationException("Conflict action availability did not update after choosing the draft.");
        await editor.SaveAssetCommand.ExecuteAsync(null);
        if (JsonNode.Parse(await File.ReadAllTextAsync(file))!["name"]!.GetValue<string>() != "Local draft")
            throw new InvalidOperationException($"Explicit draft choice did not save. Status: {editor.ProjectStatus}; draft: {editor.EditorText}");
        var cleanEdit = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        cleanEdit["name"] = "Clean external reload";
        await File.WriteAllTextAsync(file, cleanEdit.ToJsonString());
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!editor.EditorText.Contains("Clean external reload", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        if (!editor.EditorText.Contains("Clean external reload", StringComparison.Ordinal) || editor.HasConflict)
            throw new InvalidOperationException("Clean editor did not reload the external edit.");
        editor.EditorText = "{ invalid JSON";
        await Task.Delay(500);
        await using (var reader = await new ProjectRepository().OpenAsync(project, writable: false))
        {
            if ((await reader.ReadDraftAsync(asset.Path))?.Text != editor.EditorText)
                throw new InvalidOperationException("Invalid JSON editor text was not preserved in the recovery draft.");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "project-smoke.json"), "{\"draftPreserved\":true,\"conflictDecision\":true,\"cleanReload\":true,\"invalidTextRecovered\":true,\"immediateShutdownRecovered\":true,\"secondWriterReadOnly\":true,\"futureSchemaReadOnly\":true}\n");
    }

    public static async Task TestHttpAsync(MainWindow window, MainViewModel model, string directory)
    {
        Directory.CreateDirectory(directory);
        window.MainTabs.SelectedIndex = 1;
        window.RequestTabs.SelectedIndex = 1;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        const string body = "{\"message\":\"desktop binding verified\"}";
        window.RequestBodyEditor.Text = body;
        if (model.RequestBody != body) throw new InvalidOperationException("Editor two-way binding did not update the request.");
        model.Method = "POST";
        model.Url = "http://127.0.0.1:5057/echo";
        model.RequestQuery = "testKey=alpha\ntestKey=beta";
        await model.SendCommand.ExecuteAsync(null);
        if (!model.Status.StartsWith("Passed", StringComparison.Ordinal)) throw new InvalidOperationException("Desktop HTTP request failed.");
        using var response = JsonDocument.Parse(model.ResponseBody);
        if (response.RootElement.GetProperty("body").GetString() != body) throw new InvalidOperationException("HTTP body did not round trip.");
        Capture(window, Path.Combine(directory, "http-response.png"));

        // Verify workbench Close and New commands
        model.CloseRequestCommand.Execute(null);
        if (!string.IsNullOrEmpty(model.Url) || !string.IsNullOrEmpty(model.RequestBody))
            throw new InvalidOperationException("CloseRequest did not reset workbench state.");
        model.NewRequestCommand.Execute(null);
        if (model.Method != "GET" || model.RequestName != "New request")
            throw new InvalidOperationException("NewRequest did not initialize a fresh request.");

        model.Url = "http://127.0.0.1:5057/echo/{{$guid}}";
        await model.InspectVariablesCommand.ExecuteAsync(null);
        if (!model.VariablesInspectionText.Contains("$guid", StringComparison.Ordinal))
            throw new InvalidOperationException("Variable inspection failed to find dynamic helper.");

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
        window.MainTabs.SelectedIndex = 2;
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
