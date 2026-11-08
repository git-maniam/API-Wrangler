using System.Text;
using ApiWrangler.Execution;

namespace ApiWrangler.IntegrationTests;

public sealed class GitSecretScannerTests
{
    [Fact]
    public void Scan_FindsCredentialTypesWithPathAndLineWithoutReturningSecrets()
    {
        const string github = "ghp_abcdefghijklmnopqrstuvwxyz123456";
        const string aws = "AKIA1234567890ABCDEF";
        const string keyBody = "ZmFrZS1wZW0tYmxvY2stY29udGVudA==";
        var text = $"header\n{aws}\n{github}\n-----BEGIN PRIVATE KEY-----\n{keyBody}\n-----END PRIVATE KEY-----\n";

        var findings = GitSecretScanner.Scan("config/credentials.txt", Encoding.UTF8.GetBytes(text));

        Assert.Collection(findings,
            finding => Assert.Equal(("config/credentials.txt", 2, "aws-access-key-id"), (finding.Path, finding.Line, finding.Rule)),
            finding => Assert.Equal(("config/credentials.txt", 3, "github-token"), (finding.Path, finding.Line, finding.Rule)),
            finding => Assert.Equal(("config/credentials.txt", 4, "pem-private-key"), (finding.Path, finding.Line, finding.Rule)));
        Assert.All(findings, finding => Assert.Equal("Possible credential detected.", finding.Message));
        var returnedText = string.Join(" ", findings.Select(finding => $"{finding.Path} {finding.Rule} {finding.Message}"));
        Assert.DoesNotContain(aws, returnedText, StringComparison.Ordinal);
        Assert.DoesNotContain(github, returnedText, StringComparison.Ordinal);
        Assert.DoesNotContain(keyBody, returnedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_IgnoresPlaceholdersBinaryAndInvalidUtf8()
    {
        var placeholderText = "${AWS_ACCESS_KEY_ID}\n{{secret:github-token}}\n{{variable}}";

        Assert.Empty(GitSecretScanner.Scan("template.txt", Encoding.UTF8.GetBytes(placeholderText)));
        Assert.Empty(GitSecretScanner.Scan("image.bin", new byte[] { 0x41, 0x00, 0x42 }));
        Assert.Empty(GitSecretScanner.Scan("invalid.txt", new byte[] { 0xC3, 0x28 }));
    }
}
