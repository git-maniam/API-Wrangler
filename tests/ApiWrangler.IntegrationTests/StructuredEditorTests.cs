using ApiWrangler.Application;
using ApiWrangler.Storage;

namespace ApiWrangler.IntegrationTests;

public sealed class StructuredEditorTests
{
    [Theory]
    [InlineData("{\"id\":1}", "application/json", StructuredFormat.Json)]
    [InlineData("<root><elem/></root>", "application/xml", StructuredFormat.Xml)]
    [InlineData("name: test\nversion: 1", "text/yaml", StructuredFormat.Yaml)]
    [InlineData("query GetUser { user { id name } }", "application/graphql", StructuredFormat.GraphQL)]
    [InlineData("id,name,email\n1,Alice,alice@example.com", "text/csv", StructuredFormat.Csv)]
    [InlineData("Hello World", "text/plain", StructuredFormat.Raw)]
    public void StructuredTextEngine_DetectsFormatFromContentType(string text, string contentType, StructuredFormat expected)
    {
        var detected = StructuredTextEngine.DetectFormat(text, contentType);
        Assert.Equal(expected, detected);
    }

    [Theory]
    [InlineData("{\"name\":\"Alice\"}", StructuredFormat.Json)]
    [InlineData("[1, 2, 3]", StructuredFormat.Json)]
    [InlineData("<note><body>text</body></note>", StructuredFormat.Xml)]
    [InlineData("query MyQuery { hero { name } }", StructuredFormat.GraphQL)]
    [InlineData("mutation CreateUser($name: String!) { createUser(name: $name) { id } }", StructuredFormat.GraphQL)]
    [InlineData("id,name\n1,Alice\n2,Bob", StructuredFormat.Csv)]
    public void StructuredTextEngine_DetectsFormatFromContentInspection(string text, StructuredFormat expected)
    {
        var detected = StructuredTextEngine.DetectFormat(text);
        Assert.Equal(expected, detected);
    }

    [Fact]
    public void StructuredTextEngine_BeautifiesJson_ProducesIndentedOutput()
    {
        var minified = "{\"user\":{\"id\":10,\"name\":\"Alice\",\"roles\":[\"admin\",\"user\"]}}";
        var result = StructuredTextEngine.Beautify(minified, "application/json");

        Assert.True(result.Success);
        Assert.Null(result.Diagnostic);
        Assert.Equal(StructuredFormat.Json, result.Format);
        Assert.Contains("\n", result.Text);
        Assert.Contains("  \"id\": 10", result.Text);
    }

    [Fact]
    public void StructuredTextEngine_MalformedJson_ReturnsDiagnosticWithoutAlteringContent()
    {
        var malformed = "{\"user\": {\"id\": 10, \"name\": }}";
        var result = StructuredTextEngine.Beautify(malformed, "application/json");

        Assert.False(result.Success);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("JSON parse error", result.Diagnostic);
        Assert.Equal(malformed, result.Text); // Raw content preserved
    }

    [Fact]
    public void StructuredTextEngine_BeautifiesXml_ProducesIndentedOutput()
    {
        var minified = "<user id=\"1\"><name>Alice</name></user>";
        var result = StructuredTextEngine.Beautify(minified, "application/xml");

        Assert.True(result.Success);
        Assert.Null(result.Diagnostic);
        Assert.Equal(StructuredFormat.Xml, result.Format);
        Assert.Contains("  <name>Alice</name>", result.Text);
    }

    [Fact]
    public void StructuredTextEngine_MalformedXml_ReturnsDiagnosticWithoutAlteringContent()
    {
        var malformed = "<user><name>Alice</user>";
        var result = StructuredTextEngine.Beautify(malformed, "application/xml");

        Assert.False(result.Success);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("XML parse error", result.Diagnostic);
        Assert.Equal(malformed, result.Text);
    }

    [Fact]
    public void StructuredTextEngine_YamlWithTabs_ReturnsDiagnostic()
    {
        var invalidYaml = "user:\n\tname: Alice";
        var result = StructuredTextEngine.FormatYaml(invalidYaml);

        Assert.False(result.Success);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("Tabs are not permitted", result.Diagnostic);
    }

    [Fact]
    public void StructuredTextEngine_GraphQL_FormatsAndValidatesBraces()
    {
        var valid = "query { user(id: 1) { name email profile { bio } } }";
        var result = StructuredTextEngine.FormatGraphQL(valid);
        Assert.True(result.Success);
        Assert.Null(result.Diagnostic);

        var unbalanced = "query { user { name";
        var badResult = StructuredTextEngine.FormatGraphQL(unbalanced);
        Assert.False(badResult.Success);
        Assert.NotNull(badResult.Diagnostic);
        Assert.Contains("Unbalanced braces", badResult.Diagnostic);
    }

    [Fact]
    public void StructuredTextEngine_Csv_ProducesAlignedPreviewAndDetectsUnterminatedQuotes()
    {
        var csv = "ID,Name,Email\n1,Alice,alice@example.com\n222,Bob,bob@example.com";
        var result = StructuredTextEngine.FormatCsv(csv);

        Assert.True(result.Success);
        Assert.Contains("Alice", result.Text);
        Assert.Contains("bob@example.com", result.Text);

        var brokenCsv = "ID,Name\n1,\"Unclosed string";
        var brokenResult = StructuredTextEngine.FormatCsv(brokenCsv);
        Assert.False(brokenResult.Success);
        Assert.NotNull(brokenResult.Diagnostic);
        Assert.Contains("Unterminated quoted field", brokenResult.Diagnostic);
    }

    [Fact]
    public void StructuredTextEngine_EnforcesMaxSizeBytesThreshold()
    {
        var largeContent = new string('a', 6 * 1024 * 1024); // 6 MiB
        var result = StructuredTextEngine.Beautify(largeContent, "application/json", maxSizeBytes: 5 * 1024 * 1024);

        Assert.False(result.Success);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("exceeds the beautification limit", result.Diagnostic);
        Assert.Equal(largeContent.Length, result.Text.Length);
    }

    [Fact]
    public async Task LocalSettingsStore_PersistsAndRestoresFontPreferences()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ApiWrangler-font-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalSettingsStore(tempDir);
            var initial = await store.ReadAsync();
            Assert.Equal("Segoe UI", initial.EditorFontFamily);
            Assert.Equal(16.0, initial.EditorFontSize);

            var updated = initial with { EditorFontFamily = "Cascadia Code", EditorFontSize = 18.0 };
            await store.SaveAsync(updated);

            var reloaded = await store.ReadAsync();
            Assert.Equal("Cascadia Code", reloaded.EditorFontFamily);
            Assert.Equal(18.0, reloaded.EditorFontSize);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
