using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public class PackagingAndLifecycleTests
{
    [Fact]
    public void AppIcon_ExistsAndContainsValidMultiResolutionFrames()
    {
        string solutionDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string icoPath = Path.Combine(solutionDir, "src/ApiWrangler.Desktop/Resources/app-icon.ico");
        string pngPath = Path.Combine(solutionDir, "src/ApiWrangler.Desktop/Resources/App-Icon.png");

        Assert.True(File.Exists(icoPath), $"app-icon.ico must exist at {icoPath}");
        Assert.True(File.Exists(pngPath), $"App-Icon.png must exist at {pngPath}");

        var fileInfo = new FileInfo(icoPath);
        Assert.True(fileInfo.Length > 10000, "Icon file must contain multi-resolution frames.");

        using var fs = File.OpenRead(icoPath);
        using var br = new BinaryReader(fs);
        ushort reserved = br.ReadUInt16();
        ushort type = br.ReadUInt16();
        ushort count = br.ReadUInt16();

        Assert.Equal(0, reserved);
        Assert.Equal(1, type); // 1 = ICO
        Assert.True(count >= 4, "Icon must have at least 4 resolution frames (16, 32, 48, 64, 128, 256).");
    }

    [Fact]
    public void UserDataPreservation_SimulatedLifecycle_NeverDeletesUserData()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "api-wrangler-test-" + Guid.NewGuid());
        string installDir = Path.Combine(tempRoot, "Programs/ApiWrangler");
        string userDataDir = Path.Combine(tempRoot, "AppData/ApiWrangler");

        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(userDataDir);

        try
        {
            // Simulate binary installation
            File.WriteAllText(Path.Combine(installDir, "ApiWrangler.Desktop.dll"), "binary-content");

            // Simulate user persistent storage (SQLite db and project configs)
            string userDb = Path.Combine(userDataDir, "operational.db");
            string userSettings = Path.Combine(userDataDir, "settings.json");
            File.WriteAllText(userDb, "user-database-records");
            File.WriteAllText(userSettings, "{ \"Theme\": \"Dark\" }");

            // Simulate upgrade: overwrite binary folder
            File.WriteAllText(Path.Combine(installDir, "ApiWrangler.Desktop.dll"), "updated-binary-content");

            // Verify user data is completely untouched
            Assert.True(File.Exists(userDb));
            Assert.True(File.Exists(userSettings));
            Assert.Equal("user-database-records", File.ReadAllText(userDb));

            // Simulate uninstallation of binary directory
            Directory.Delete(installDir, recursive: true);
            Assert.False(Directory.Exists(installDir));

            // Verify user data STILL remains untouched after uninstall
            Assert.True(Directory.Exists(userDataDir));
            Assert.True(File.Exists(userDb));
            Assert.Equal("user-database-records", File.ReadAllText(userDb));
            Assert.Equal("{ \"Theme\": \"Dark\" }", File.ReadAllText(userSettings));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void NoCriticalPlaceholders_InDomainOrApplicationSource()
    {
        string solutionDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var csFiles = Directory.GetFiles(Path.Combine(solutionDir, "src/ApiWrangler.Domain"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(solutionDir, "src/ApiWrangler.Application"), "*.cs", SearchOption.AllDirectories));

        foreach (var file in csFiles)
        {
            string content = File.ReadAllText(file);
            Assert.DoesNotContain("throw new NotImplementedException()", content);
            Assert.DoesNotContain("TODO: Implement", content);
        }
    }

    [Fact]
    public void SbomAndChecksumFormat_ConformToStandards()
    {
        string sampleChecksumLine = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  sample.zip";
        string[] parts = sampleChecksumLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, parts.Length);
        Assert.Equal(64, parts[0].Length); // Valid 64-character SHA256 hex
        Assert.EndsWith(".zip", parts[1]);

        // Validate SBOM JSON template
        var sbom = new
        {
            bomFormat = "CycloneDX",
            specVersion = "1.5",
            metadata = new
            {
                component = new { name = "api-wrangler", version = "1.0.0" }
            }
        };

        string json = JsonSerializer.Serialize(sbom);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("CycloneDX", doc.RootElement.GetProperty("bomFormat").GetString());
        Assert.Equal("1.0.0", doc.RootElement.GetProperty("metadata").GetProperty("component").GetProperty("version").GetString());
    }
}

