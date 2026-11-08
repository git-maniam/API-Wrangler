using System.Text;
using System.Text.RegularExpressions;

namespace ApiWrangler.Execution;

public sealed record GitSecretFinding(string Path, int Line, string Rule, string Message);

/// <summary>Finds a small set of high-confidence credentials in UTF-8 repository text.</summary>
public static class GitSecretScanner
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex AwsAccessKey = new(@"(?<![A-Za-z0-9])AKIA[A-Z0-9]{16}(?![A-Za-z0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex GitHubToken = new(@"(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    private static readonly Regex PemPrivateKey = new(
        @"-----BEGIN (?<type>(?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY|ENCRYPTED PRIVATE KEY)-----[\s\S]*?-----END \k<type>-----",
        RegexOptions.CultureInvariant);

    /// <summary>Scans bytes only when they form non-binary, strictly valid UTF-8 text.</summary>
    public static IReadOnlyList<GitSecretFinding> Scan(string path, ReadOnlySpan<byte> content)
    {
        if (content.Contains((byte)0))
        {
            return Array.Empty<GitSecretFinding>();
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Array.Empty<GitSecretFinding>();
        }

        var findings = new List<GitSecretFinding>();
        AddMatches(path, AwsAccessKey, "aws-access-key-id", text, findings);
        AddMatches(path, GitHubToken, "github-token", text, findings);
        AddMatches(path, PemPrivateKey, "pem-private-key", text, findings);
        return findings.OrderBy(finding => finding.Line).ThenBy(finding => finding.Rule, StringComparer.Ordinal).ToArray();
    }

    private static void AddMatches(string path, Regex pattern, string rule, string text, List<GitSecretFinding> findings)
    {
        var reportedLines = new HashSet<int>();
        foreach (Match match in pattern.Matches(text))
        {
            var line = 1;
            for (var index = 0; index < match.Index; index++)
            {
                if (text[index] == '\n')
                {
                    line++;
                }
            }

            if (reportedLines.Add(line))
            {
                findings.Add(new GitSecretFinding(path, line, rule, "Possible credential detected."));
            }
        }
    }
}
