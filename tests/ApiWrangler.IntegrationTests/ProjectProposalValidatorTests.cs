using System.Text;
using ApiWrangler.Storage;

namespace ApiWrangler.IntegrationTests;

public sealed class ProjectProposalValidatorTests
{
    [Fact]
    public async Task Validate_OverlayRepairsConflictWithoutChangingDisk_AndRejectsBrokenReferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "ApiWrangler-proposal-tests", Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project");
        const string requestPath = "collections/default/requests/hello.request.json";
        try
        {
            await using (var session = await new ProjectRepository(Path.Combine(root, "state")).InitializeAsync(project, "Proposal")) { }
            string full = Path.Combine(project, requestPath);
            byte[] original = await File.ReadAllBytesAsync(full);
            byte[] conflict = Encoding.UTF8.GetBytes("<<<<<<< HEAD\nlocal\n=======\nincoming\n>>>>>>> team\n");
            await File.WriteAllBytesAsync(full, conflict);
            var proposal = new Dictionary<string, byte[]?> { [requestPath] = original };
            Assert.True((await ProjectProposalValidator.ValidateAsync(project, proposal)).IsValid);
            Assert.Equal(conflict, await File.ReadAllBytesAsync(full));

            var invalid = CanonicalJson.Parse(original);
            invalid["collectionId"] = Guid.NewGuid().ToString();
            proposal[requestPath] = CanonicalJson.Serialize(invalid);
            var rejected = await ProjectProposalValidator.ValidateAsync(project, proposal);
            Assert.False(rejected.IsValid);
            Assert.Contains(rejected.Diagnostics, d => d.Pointer == "/collectionId");
            proposal[requestPath] = null;
            Assert.False((await ProjectProposalValidator.ValidateAsync(project, proposal)).IsValid);
            Assert.Equal(conflict, await File.ReadAllBytesAsync(full));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Validate_MultipleCandidatesCanRepairReferencesTogether_AndRejectDuplicateIds()
    {
        string root = Path.Combine(Path.GetTempPath(), "ApiWrangler-proposal-tests", Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project");
        const string requestPath = "collections/default/requests/hello.request.json";
        const string collectionPath = "collections/default/collection.json";
        try
        {
            await using (var session = await new ProjectRepository(Path.Combine(root, "state")).InitializeAsync(project, "Proposal")) { }
            var collection = CanonicalJson.Parse(await File.ReadAllBytesAsync(Path.Combine(project, collectionPath)));
            collection["requestIds"] = new System.Text.Json.Nodes.JsonArray();
            var proposed = new Dictionary<string, byte[]?> { [requestPath] = null, [collectionPath] = CanonicalJson.Serialize(collection) };
            Assert.True((await ProjectProposalValidator.ValidateAsync(project, proposed)).IsValid);
            proposed[requestPath] = await File.ReadAllBytesAsync(Path.Combine(project, requestPath));
            proposed["collections/default/requests/duplicate.request.json"] = proposed[requestPath];
            var result = await ProjectProposalValidator.ValidateAsync(project, proposed);
            Assert.False(result.IsValid);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate entity ID", StringComparison.Ordinal));
            await Assert.ThrowsAsync<IOException>(() => ProjectProposalValidator.ValidateAsync(project,
                new Dictionary<string, byte[]?> { ["../outside.json"] = [] }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
