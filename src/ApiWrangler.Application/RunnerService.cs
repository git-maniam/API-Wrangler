using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class RunnerEnrollmentService
{
    private readonly ConcurrentDictionary<string, RunnerEnrollmentToken> tokens = new();

    public EnrollmentResult CreateToken(
        string name,
        RunnerEnrollmentScope scope,
        TimeSpan? validity = null,
        IReadOnlyList<string>? allowedCapabilities = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new EnrollmentResult(false, ErrorMessage: "Token name cannot be empty.");
        }

        var tokenId = Guid.NewGuid().ToString("N");
        var secretBytes = new byte[32];
        RandomNumberGenerator.Fill(secretBytes);
        var secretPart = Convert.ToHexString(secretBytes).ToLowerInvariant();
        var plaintextToken = $"awr_tok_{tokenId}_{secretPart}";

        var hashedSecret = HashSecret(plaintextToken);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = validity.HasValue ? now.Add(validity.Value) : (DateTimeOffset?)null;

        var tokenRecord = new RunnerEnrollmentToken(
            TokenId: tokenId,
            Name: name,
            Scope: scope,
            HashedSecret: hashedSecret,
            CreatedAt: now,
            ExpiresAt: expiresAt,
            IsRevoked: false,
            AllowedCapabilities: allowedCapabilities
        );

        tokens[tokenId] = tokenRecord;
        return new EnrollmentResult(true, PlaintextToken: plaintextToken, TokenId: tokenId);
    }

    public bool ValidateToken(
        string plaintextToken,
        RunnerEnrollmentScope requiredScope,
        out RunnerEnrollmentToken? matchedToken)
    {
        matchedToken = null;
        if (string.IsNullOrWhiteSpace(plaintextToken) || !plaintextToken.StartsWith("awr_tok_"))
        {
            return false;
        }

        var parts = plaintextToken.Split('_');
        if (parts.Length < 4)
        {
            return false;
        }

        var tokenId = parts[2];
        if (!tokens.TryGetValue(tokenId, out var record))
        {
            return false;
        }

        if (record.IsRevoked)
        {
            return false;
        }

        if (record.ExpiresAt.HasValue && DateTimeOffset.UtcNow > record.ExpiresAt.Value)
        {
            return false;
        }

        // Constant-time hash verification to prevent timing side channels
        var computedHash = HashSecret(plaintextToken);
        var expectedHashBytes = Encoding.UTF8.GetBytes(record.HashedSecret);
        var computedHashBytes = Encoding.UTF8.GetBytes(computedHash);

        if (!CryptographicOperations.FixedTimeEquals(expectedHashBytes, computedHashBytes))
        {
            return false;
        }

        // Scope check: Admin inherits all permissions
        if (record.Scope != RunnerEnrollmentScope.Admin && record.Scope != requiredScope)
        {
            return false;
        }

        matchedToken = record;
        return true;
    }

    public bool RevokeToken(string tokenId)
    {
        if (tokens.TryGetValue(tokenId, out var record))
        {
            tokens[tokenId] = record with { IsRevoked = true };
            return true;
        }
        return false;
    }

    public EnrollmentResult RotateToken(string oldTokenId, TimeSpan? newValidity = null)
    {
        if (!tokens.TryGetValue(oldTokenId, out var existing))
        {
            return new EnrollmentResult(false, ErrorMessage: "Token not found.");
        }

        // Revoke the previous token
        tokens[oldTokenId] = existing with { IsRevoked = true };

        // Issue new token with identical metadata and updated validity
        return CreateToken(
            name: $"{existing.Name} (Rotated)",
            scope: existing.Scope,
            validity: newValidity,
            allowedCapabilities: existing.AllowedCapabilities
        );
    }

    public static string HashSecret(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class ExactCommitJobExecutor
{
    private readonly string runnerId;
    private readonly string runnerVersion;

    public ExactCommitJobExecutor(string? runnerId = null, string? runnerVersion = null)
    {
        this.runnerId = runnerId ?? $"runner-{Environment.MachineName.ToLowerInvariant()}";
        this.runnerVersion = runnerVersion ?? "1.0.0-unattended";
    }

    public async Task<RunnerJobResult> ExecuteJobAsync(
        RunnerJobDefinition job,
        string isolatedRootDirectory,
        Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<int>>? executionDelegate = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var jobDir = Path.Combine(isolatedRootDirectory, "jobs", job.JobId);
        Directory.CreateDirectory(jobDir);

        // 1. Exact commit SHA validation
        if (string.IsNullOrWhiteSpace(job.ExactCommitSha) || job.ExactCommitSha.Length < 7)
        {
            return new RunnerJobResult(
                JobId: job.JobId,
                Outcome: RunnerJobOutcome.Failed,
                ExitCode: 2,
                Summary: "Invalid job definition: ExactCommitSha must be specified and at least 7 hex characters.",
                ArtifactPaths: Array.Empty<string>(),
                ExecutedCommitSha: string.Empty,
                StartedAt: startedAt,
                CompletedAt: DateTimeOffset.UtcNow,
                RunnerId: runnerId
            );
        }

        // 2. Write execution manifest recording exact commit, engine version, and timestamp
        var manifestPath = Path.Combine(jobDir, "execution-manifest.json");
        var manifest = new
        {
            job.JobId,
            job.ProjectId,
            job.RepositoryUrl,
            job.ExactCommitSha,
            RunnerId = runnerId,
            RunnerVersion = runnerVersion,
            StartedAt = startedAt.ToString("O"),
            DedicatedAccount = Environment.UserName,
            HostMachine = Environment.MachineName
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

        // 3. Resolve host-local secrets independently without requiring desktop credentials
        var hostSecrets = ResolveHostSecrets();

        // 4. Execute payload within isolated directory
        try
        {
            int exitCode = 0;
            if (executionDelegate is not null)
            {
                exitCode = await executionDelegate(jobDir, hostSecrets, cancellationToken).ConfigureAwait(false);
            }

            var outcome = exitCode == 0 ? RunnerJobOutcome.Passed : RunnerJobOutcome.Failed;
            return new RunnerJobResult(
                JobId: job.JobId,
                Outcome: outcome,
                ExitCode: exitCode,
                Summary: outcome == RunnerJobOutcome.Passed ? "Job completed successfully." : $"Job exited with code {exitCode}.",
                ArtifactPaths: new[] { manifestPath },
                ExecutedCommitSha: job.ExactCommitSha,
                StartedAt: startedAt,
                CompletedAt: DateTimeOffset.UtcNow,
                RunnerId: runnerId
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RunnerJobResult(
                JobId: job.JobId,
                Outcome: RunnerJobOutcome.Cancelled,
                ExitCode: 4,
                Summary: "Job execution was cancelled.",
                ArtifactPaths: new[] { manifestPath },
                ExecutedCommitSha: job.ExactCommitSha,
                StartedAt: startedAt,
                CompletedAt: DateTimeOffset.UtcNow,
                RunnerId: runnerId
            );
        }
        catch (Exception ex)
        {
            // Crashed job: Potentially mutating operations must not be silently retried.
            // Mark outcome indeterminate with diagnostics.
            return new RunnerJobResult(
                JobId: job.JobId,
                Outcome: RunnerJobOutcome.Indeterminate,
                ExitCode: 3,
                Summary: $"Job aborted unexpectedly: {ex.Message}",
                ArtifactPaths: new[] { manifestPath },
                ExecutedCommitSha: job.ExactCommitSha,
                StartedAt: startedAt,
                CompletedAt: DateTimeOffset.UtcNow,
                RunnerId: runnerId,
                IndeterminateReason: $"Process terminated unexpectedly with {ex.GetType().Name}. Potential mutating operations may have partially succeeded. Manual review required."
            );
        }
    }

    public static IReadOnlyDictionary<string, string> ResolveHostSecrets()
    {
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
        {
            var key = env.Key?.ToString();
            var val = env.Value?.ToString();
            if (string.IsNullOrEmpty(key) || val is null) continue;

            if (key.StartsWith("API_WRANGLER_SECRET_", StringComparison.OrdinalIgnoreCase))
            {
                var secretKey = key.Substring("API_WRANGLER_SECRET_".Length);
                secrets[secretKey] = val;
            }
        }

        return secrets;
    }

    public static bool MatchCapabilities(
        IReadOnlyList<string> runnerCapabilities,
        IReadOnlyList<string>? requiredCapabilities)
    {
        if (requiredCapabilities is null || requiredCapabilities.Count == 0)
        {
            return true;
        }

        var runnerSet = new HashSet<string>(runnerCapabilities, StringComparer.OrdinalIgnoreCase);
        return requiredCapabilities.All(req => runnerSet.Contains(req));
    }
}

