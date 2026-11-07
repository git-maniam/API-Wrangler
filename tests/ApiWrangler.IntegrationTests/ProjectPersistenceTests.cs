using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;
using System.Text.Json;

namespace ApiWrangler.IntegrationTests;

public sealed class ProjectPersistenceTests
{
    [Fact]
    public async Task Save_ReplacementFailure_RollsBackAllFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var stateRoot = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await new ProjectRepository(stateRoot).InitializeAsync(projectRoot, "Fixture");
            var files = new[]
            {
                "collections/default/collection.json",
                "collections/default/requests/hello.request.json"
            };
            var changes = new List<FileChange>();
            var originals = new List<byte[]>();
            foreach (var relative in files)
            {
                var path = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                var original = await File.ReadAllBytesAsync(path);
                originals.Add(original);
                changes.Add(new(relative, original.Concat(new byte[] { (byte)' ' }).ToArray(), CanonicalJson.Hash(original)));
            }
            var transaction = new FileTransactions(projectRoot, stateRoot)
            {
                AfterReplacement = index =>
                {
                    if (index == 0) throw new IOException("Simulated process-time replacement failure.");
                }
            };

            await Assert.ThrowsAsync<IOException>(() => transaction.ApplyAsync(changes, CancellationToken.None));

            for (var index = 0; index < files.Length; index++)
                Assert.Equal(originals[index], await File.ReadAllBytesAsync(Path.Combine(projectRoot, files[index].Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Discover_RepositoryRootWithNestedProject_FindsApiWranglerDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(root, "repository");
        var projectRoot = Path.Combine(repositoryRoot, "api-wrangler");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        try
        {
            await using var session = await new ProjectRepository(Path.Combine(root, "state")).InitializeAsync(projectRoot, "Fixture");

            Assert.Equal(Path.GetFullPath(projectRoot), ProjectRepository.Discover(repositoryRoot));
            Assert.Equal(Path.GetFullPath(projectRoot), ProjectRepository.Discover(Path.Combine(projectRoot, "wrangler.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Watch_BufferOverflow_RequestsFullRescan()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var session = (ProjectSession)await new ProjectRepository(Path.Combine(root, "state"))
                .InitializeAsync(Path.Combine(root, "project"), "Fixture");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var changes = session.WatchAsync(timeout.Token).GetAsyncEnumerator();

            session.OnWatcherError(session, new ErrorEventArgs(new InternalBufferOverflowException()));

            Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            Assert.True(changes.Current);
            Assert.True((await session.ReadAsync(timeout.Token)).IsValid);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Watch_ExternalEdit_PreservesDirtyDraftAndRequiresConflictResolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await new ProjectRepository(Path.Combine(root, "state")).InitializeAsync(projectRoot, "Fixture");
            var path = "collections/default/requests/hello.request.json";
            var asset = (await session.ReadAsync()).Assets.Single(item => item.Path == path);
            var dirtyContent = (JsonObject)asset.Content.DeepClone();
            dirtyContent["name"] = "Unsaved editor draft";
            var draft = new EditorDraft(path, asset.Hash, dirtyContent);
            await session.SaveDraftAsync(draft);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var changes = session.WatchAsync(timeout.Token).GetAsyncEnumerator();
            var externalContent = (JsonObject)asset.Content.DeepClone();
            externalContent["name"] = "External change";
            var fullPath = Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(fullPath, CanonicalJson.Serialize(externalContent));

            Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            var preserved = await session.ReadDraftAsync(path, timeout.Token);
            Assert.NotNull(preserved);
            Assert.Equal(asset.Hash, preserved.BaseHash);
            Assert.Equal("Unsaved editor draft", preserved.Content["name"]?.GetValue<string>());
            await Assert.ThrowsAsync<ProjectConflictException>(() => session.SaveAsync([new(path, preserved.Content, preserved.BaseHash)]));
            Assert.Equal("Unsaved editor draft", (await session.ReadDraftAsync(path))!.Content["name"]?.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalSettings_SaveAndReload_PreservesLastProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalSettingsStore(root);
            var settings = new LocalSettings("D:/work/my-api-project");

            await store.SaveAsync(settings);

            Assert.Equal(settings, await store.ReadAsync());
            Assert.True(File.Exists(Path.Combine(root, "desktop.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Read_MissingRequiredField_ReportsFileAndFieldPointer()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await new ProjectRepository(Path.Combine(root, "state")).InitializeAsync(projectRoot, "Fixture");
            var path = "collections/default/requests/hello.request.json";
            var assetPath = Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var invalid = CanonicalJson.Parse(await File.ReadAllBytesAsync(assetPath));
            invalid.Remove("method");
            await File.WriteAllBytesAsync(assetPath, CanonicalJson.Serialize(invalid));

            var view = await session.ReadAsync();

            Assert.Contains(view.Diagnostics, diagnostic => diagnostic.File == path && diagnostic.Pointer == "/method" && diagnostic.Message.Contains("method", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_ExternalEditInTransaction_PreservesAllCurrentFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var stateRoot = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await new ProjectRepository(stateRoot).InitializeAsync(projectRoot, "Fixture");
            var paths = new[]
            {
                "collections/default/collection.json",
                "collections/default/requests/hello.request.json"
            };
            var id = Guid.NewGuid().ToString("N");
            var journalDirectory = Path.Combine(stateRoot, "transactions", id);
            Directory.CreateDirectory(journalDirectory);
            var entries = new List<JournalEntry>();
            var replacementBytes = new List<byte[]>();
            for (var index = 0; index < paths.Length; index++)
            {
                var relative = paths[index];
                var target = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                var original = await File.ReadAllBytesAsync(target);
                var replacement = original.Concat(new byte[] { (byte)' ' }).ToArray();
                var stage = Path.GetDirectoryName(relative)!.Replace('\\', '/') + $"/.awr-{id}-{index}.tmp";
                entries.Add(new(relative, CanonicalJson.Hash(original), CanonicalJson.Hash(replacement), stage, $"{index}.before"));
                replacementBytes.Add(replacement);
                await FileTransactions.WriteDurableAsync(Path.Combine(journalDirectory, $"{index}.before"), original, CancellationToken.None);
                await FileTransactions.WriteDurableAsync(Path.Combine(projectRoot, stage.Replace('/', Path.DirectorySeparatorChar)), replacement, CancellationToken.None);
            }
            await File.WriteAllBytesAsync(Path.Combine(journalDirectory, "journal.json"),
                JsonSerializer.SerializeToUtf8Bytes(new WriteJournal("prepared", entries)));
            File.Move(Path.Combine(projectRoot, entries[0].Stage.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(projectRoot, entries[0].Path.Replace('/', Path.DirectorySeparatorChar)), overwrite: true);
            var externalEdit = System.Text.Encoding.UTF8.GetBytes("external edit");
            await File.WriteAllBytesAsync(Path.Combine(projectRoot, entries[1].Path.Replace('/', Path.DirectorySeparatorChar)), externalEdit);

            await Assert.ThrowsAsync<ProjectConflictException>(() => new FileTransactions(projectRoot, stateRoot).RecoverAsync(CancellationToken.None));

            Assert.Equal(replacementBytes[0], await File.ReadAllBytesAsync(Path.Combine(projectRoot, entries[0].Path.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(externalEdit, await File.ReadAllBytesAsync(Path.Combine(projectRoot, entries[1].Path.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(File.Exists(Path.Combine(projectRoot, entries[1].Stage.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Watch_ExternalProjectEdit_EmitsDebouncedChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var repository = new ProjectRepository(Path.Combine(root, "state"));
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await repository.InitializeAsync(projectRoot, "Fixture");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var changes = session.WatchAsync(timeout.Token).GetAsyncEnumerator();
            var requestPath = Path.Combine(projectRoot, "collections", "default", "requests", "hello.request.json");
            var request = CanonicalJson.Parse(await File.ReadAllBytesAsync(requestPath));
            request["name"] = "Edited externally";
            await File.WriteAllBytesAsync(requestPath, CanonicalJson.Serialize(request));

            Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            Assert.True(changes.Current);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_ExtensionFieldsRemainCanonical_AndUnchangedSaveProducesNoDiff()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var repository = new ProjectRepository(Path.Combine(root, "state"));
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await repository.InitializeAsync(projectRoot, "Fixture");
            var request = (await session.ReadAsync()).Assets.Single(asset => asset.Kind == AssetKind.Request);
            var edited = (JsonObject)request.Content.DeepClone();
            edited["vendorExtension"] = new JsonObject { ["enabled"] = true, ["label"] = "preserved" };
            await session.SaveAsync([new(request.Path, edited, request.Hash)]);

            var path = Path.Combine(projectRoot, request.Path.Replace('/', Path.DirectorySeparatorChar));
            var firstSave = await File.ReadAllBytesAsync(path);
            var text = System.Text.Encoding.UTF8.GetString(firstSave);
            Assert.EndsWith("\n", text);
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
            Assert.True(text.IndexOf("\"id\"", StringComparison.Ordinal) < text.IndexOf("\"vendorExtension\"", StringComparison.Ordinal));
            Assert.Equal("preserved", CanonicalJson.Parse(firstSave)["vendorExtension"]?["label"]?.GetValue<string>());

            var saved = (await session.ReadAsync()).Assets.Single(asset => asset.Path == request.Path);
            await session.SaveAsync([new(saved.Path, saved.Content.DeepClone().AsObject(), saved.Hash)]);
            Assert.Equal(firstSave, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Open_SecondWriterIsReadOnly_AndExternalEditsCauseConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var repository = new ProjectRepository(Path.Combine(root, "state"));
        Directory.CreateDirectory(root);
        try
        {
            await using var writer = await repository.InitializeAsync(projectRoot, "Fixture");
            await using var second = await repository.OpenAsync(projectRoot);
            Assert.False(writer.IsReadOnly);
            Assert.True(second.IsReadOnly);
            await Assert.ThrowsAsync<ProjectReadOnlyException>(() => second.SaveAsync([]));

            var asset = (await writer.ReadAsync()).Assets.Single(item => item.Kind == AssetKind.Request);
            var external = (JsonObject)asset.Content.DeepClone();
            external["name"] = "Changed outside this session";
            await File.WriteAllBytesAsync(Path.Combine(projectRoot, asset.Path.Replace('/', Path.DirectorySeparatorChar)), CanonicalJson.Serialize(external));
            var local = (JsonObject)asset.Content.DeepClone();
            local["name"] = "Local edit";

            await Assert.ThrowsAsync<ProjectConflictException>(() => writer.SaveAsync([new(asset.Path, local, asset.Hash)]));

            await writer.DisposeAsync();
            await using var replacement = await repository.OpenAsync(projectRoot);
            Assert.False(replacement.IsReadOnly);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Snapshot_CapturesExactProjectFiles_WithoutSecretValuesInMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var repository = new ProjectRepository(Path.Combine(root, "state"));
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await repository.InitializeAsync(projectRoot, "Fixture");
            var environment = (await session.ReadAsync()).Assets.Single(asset => asset.Kind == AssetKind.Environment);
            var updated = (JsonObject)environment.Content.DeepClone();
            updated["secrets"]!["token"] = new JsonObject { ["reference"] = "secret:token" };
            await session.SaveAsync([new(environment.Path, updated, environment.Hash)]);

            var snapshot = await session.SnapshotAsync();
            var metadata = await File.ReadAllTextAsync(Path.Combine(snapshot.Directory, "snapshot.json"));

            Assert.DoesNotContain("secret:token", metadata, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(snapshot.Directory, environment.Path.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Contains("secret:token", await File.ReadAllTextAsync(Path.Combine(snapshot.Directory, environment.Path.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(snapshot.FileCount, Directory.EnumerateFiles(snapshot.Directory, "*", SearchOption.AllDirectories).Count() - 1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_PreparedMultiFileTransaction_RestoresEveryOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var stateRoot = Path.Combine(root, "state");
        var repository = new ProjectRepository(stateRoot);
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await repository.InitializeAsync(projectRoot, "Fixture");
            var paths = new[]
            {
                "collections/default/collection.json",
                "collections/default/requests/hello.request.json"
            };
            var transactionId = Guid.NewGuid().ToString("N");
            var journalDirectory = Path.Combine(stateRoot, "transactions", transactionId);
            Directory.CreateDirectory(journalDirectory);
            var entries = new List<JournalEntry>();
            for (var index = 0; index < paths.Length; index++)
            {
                var relative = paths[index];
                var target = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                var oldBytes = await File.ReadAllBytesAsync(target);
                var newBytes = oldBytes.Concat(new byte[] { (byte)' ' }).ToArray();
                var stage = Path.GetDirectoryName(relative)!.Replace('\\', '/') + $"/.awr-{transactionId}-{index}.tmp";
                entries.Add(new(relative, CanonicalJson.Hash(oldBytes), CanonicalJson.Hash(newBytes), stage, $"{index}.before"));
                await FileTransactions.WriteDurableAsync(Path.Combine(journalDirectory, $"{index}.before"), oldBytes, CancellationToken.None);
                await FileTransactions.WriteDurableAsync(Path.Combine(projectRoot, stage.Replace('/', Path.DirectorySeparatorChar)), newBytes, CancellationToken.None);
            }
            var journal = new WriteJournal("prepared", entries);
            await File.WriteAllBytesAsync(Path.Combine(journalDirectory, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(journal));
            var first = entries[0];
            File.Move(Path.Combine(projectRoot, first.Stage.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(projectRoot, first.Path.Replace('/', Path.DirectorySeparatorChar)), overwrite: true);

            await new FileTransactions(projectRoot, stateRoot).RecoverAsync(CancellationToken.None);
            await new FileTransactions(projectRoot, stateRoot).RecoverAsync(CancellationToken.None);

            foreach (var entry in entries)
            {
                var target = Path.Combine(projectRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(entry.OldHash, CanonicalJson.Hash(await File.ReadAllBytesAsync(target)));
                Assert.False(File.Exists(Path.Combine(projectRoot, entry.Stage.Replace('/', Path.DirectorySeparatorChar))));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_NewMajorSchemaVersion_AllowsInspectionAndRejectsEditing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-project-tests", Guid.NewGuid().ToString("N"));
        var repository = new ProjectRepository(Path.Combine(root, "state"));
        var projectRoot = Path.Combine(root, "project");
        Directory.CreateDirectory(root);
        try
        {
            await using var session = await repository.InitializeAsync(projectRoot, "Fixture");
            var requestPath = Path.Combine(projectRoot, "collections", "default", "requests", "hello.request.json");
            var upgraded = CanonicalJson.Parse(await File.ReadAllBytesAsync(requestPath));
            upgraded["schemaVersion"] = 2;
            upgraded["futureField"] = new JsonObject { ["preserved"] = true };
            var bytes = CanonicalJson.Serialize(upgraded);
            await File.WriteAllBytesAsync(requestPath, bytes);

            var view = await session.ReadAsync();
            Assert.Contains(view.Assets, asset => asset.Path == "collections/default/requests/hello.request.json");
            Assert.Contains(view.Diagnostics, diagnostic => diagnostic.File == "collections/default/requests/hello.request.json");

            var asset = view.Assets.Single(item => item.Path == "collections/default/requests/hello.request.json");
            var edited = (JsonObject)asset.Content.DeepClone();
            edited["name"] = "Changed";
            var error = await Assert.ThrowsAsync<ProjectValidationException>(() =>
                session.SaveAsync([new(asset.Path, edited, asset.Hash)]));

            var diagnostic = Assert.Single(error.Diagnostics);
            Assert.Equal(asset.Path, diagnostic.File);
            Assert.Equal("/schemaVersion", diagnostic.Pointer);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(requestPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
