using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public class AiAssistanceTests
{
    private sealed class MockAiHttpMessageHandler : HttpMessageHandler
    {
        public string? LastCapturedPrompt { get; private set; }
        public string MockResponseContent { get; set; } = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"// Candidate Assertions\\nassert.strictEqual(response.status, 200);\"}}]}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                LastCapturedPrompt = request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MockResponseContent, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Ai_DisabledUntilConfigured_ThrowsDescriptiveException_WithoutNetworkCalls()
    {
        // Criterion: AI is disabled until configured and the product remains useful without it.
        var disabledConfig = new AiConfiguration(IsEnabled: false, ProviderKind: AiProviderKind.Disabled);
        var mockHandler = new MockAiHttpMessageHandler();
        using var client = new HttpClient(mockHandler);
        var service = new AiAssistanceService(disabledConfig, client);

        var items = new List<AiContextItem>
        {
            new(AiContextItemKind.RequestUrl, "Target", "https://api.example.com/items")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DraftTestsAsync(items));

        Assert.Contains("disabled until configured", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(mockHandler.LastCapturedPrompt);
    }

    [Fact]
    public async Task Ai_GitCredentialsAndSecrets_NeverEnterPrompts_AndAreExplicitlyRedacted()
    {
        // Criterion: Only explicitly selected redacted context is sent; Git credentials never enter prompts.
        var enabledConfig = new AiConfiguration(
            IsEnabled: true,
            ProviderKind: AiProviderKind.OpenAiCompatible,
            EndpointUrl: "https://api.openai.local/v1/chat/completions",
            ApiKey: "user-provided-key"
        );

        var mockHandler = new MockAiHttpMessageHandler();
        using var client = new HttpClient(mockHandler);
        var service = new AiAssistanceService(enabledConfig, client);

        string gitPat = "ghp_1234567890abcdefghijklmnopqrstuvwxyz";
        string gitUrl = "https://git-user:super-secret-pat@github.com/org/repo.git";
        string privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nMIIEowIBAAKCAQEA...\n-----END OPENSSH PRIVATE KEY-----";
        string bearerToken = "Bearer secret-jwt-token-abcdef1234567890";

        var contextItems = new List<AiContextItem>
        {
            new(AiContextItemKind.RequestHeaders, "Authorization", bearerToken),
            new(AiContextItemKind.UserNotes, "Git Remote", gitUrl),
            new(AiContextItemKind.UserNotes, "Personal Access Token", gitPat),
            new(AiContextItemKind.UserNotes, "Key", privateKey)
        };

        var proposal = await service.DraftTestsAsync(contextItems);

        Assert.NotNull(mockHandler.LastCapturedPrompt);
        string captured = mockHandler.LastCapturedPrompt;

        // Raw Git credentials must NEVER appear in prompt payload
        Assert.DoesNotContain(gitPat, captured);
        Assert.DoesNotContain("super-secret-pat", captured);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA", captured);
        Assert.DoesNotContain("secret-jwt-token-abcdef1234567890", captured);

        // Explicit redaction placeholders must be present
        Assert.Contains("[REDACTED_GIT_CREDENTIAL]", captured);
        Assert.Contains("[REDACTED_PRIVATE_KEY]", captured);
        Assert.Contains("[REDACTED_SECRET]", captured);
    }

    [Fact]
    public async Task Ai_GeneratedDiffs_RequireUserReview_AndNeverAutoExecuteOrAutoCommit()
    {
        // Criterion: Generated diffs require review; generated code never runs or commits automatically.
        var enabledConfig = new AiConfiguration(
            IsEnabled: true,
            ProviderKind: AiProviderKind.OpenAiCompatible,
            EndpointUrl: "https://api.openai.local/v1/chat/completions"
        );

        var mockHandler = new MockAiHttpMessageHandler
        {
            MockResponseContent = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"// Test Assertion\\nassert.equal(res.status, 200);\"}}]}"
        };
        using var client = new HttpClient(mockHandler);
        var service = new AiAssistanceService(enabledConfig, client);

        var items = new List<AiContextItem>
        {
            new(AiContextItemKind.RequestUrl, "Url", "https://api.example.com/resource")
        };

        var proposal = await service.DraftTestsAsync(items);

        // Verify review safety contracts
        Assert.True(proposal.RequiresUserReview);
        Assert.False(proposal.CanAutoExecute);
        Assert.False(proposal.CanAutoCommit);
        Assert.Equal(AiReviewStatus.PendingReview, proposal.Status);

        // Verify unified diff generation
        Assert.Contains("--- a/tests/drafted-assertions.js", proposal.UnifiedDiff);
        Assert.Contains("+++ b/tests/drafted-assertions.js", proposal.UnifiedDiff);
        Assert.Contains("+assert.equal(res.status, 200);", proposal.UnifiedDiff);

        // Attempting auto-execution throws
        var execEx = Assert.Throws<InvalidOperationException>(() => proposal.AttemptAutoExecute());
        Assert.Contains("requires user review and cannot run automatically", execEx.Message);

        // Attempting auto-commit throws
        var commitEx = Assert.Throws<InvalidOperationException>(() => proposal.AttemptAutoCommit());
        Assert.Contains("requires user review and cannot commit automatically", commitEx.Message);

        // Explicit review lifecycle
        proposal.Approve();
        Assert.Equal(AiReviewStatus.Approved, proposal.Status);

        proposal.Reject();
        Assert.Equal(AiReviewStatus.Rejected, proposal.Status);
    }

    [Fact]
    public async Task Ai_DatasetGeneration_AndResponseExplanation_WorkCleanly()
    {
        var enabledConfig = new AiConfiguration(
            IsEnabled: true,
            ProviderKind: AiProviderKind.LocalEndpoint,
            EndpointUrl: "http://localhost:11434/v1/chat/completions"
        );

        var mockHandler = new MockAiHttpMessageHandler
        {
            MockResponseContent = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"id,username,email\\n1,alice,alice@example.com\\n2,bob,bob@example.com\"}}]}"
        };
        using var client = new HttpClient(mockHandler);
        var service = new AiAssistanceService(enabledConfig, client);

        // 1. Dataset generation
        var datasetProposal = await service.GenerateDatasetAsync(new[] { "id", "username", "email" }, 2, "User registration test cases");
        Assert.Equal(AiProposalKind.DatasetGeneration, datasetProposal.Kind);
        Assert.Contains("alice@example.com", datasetProposal.ProposedText);
        Assert.True(datasetProposal.RequiresUserReview);

        // 2. Response explanation
        mockHandler.MockResponseContent = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"### Analysis\\nStatus 404 indicates missing endpoint resource.\"}}]}";
        var explainProposal = await service.ExplainResponseAsync(new[]
        {
            new AiContextItem(AiContextItemKind.ResponseStatusCode, "Status", "404 Not Found")
        });
        Assert.Equal(AiProposalKind.ResponseExplanation, explainProposal.Kind);
        Assert.Contains("404 indicates missing endpoint resource", explainProposal.ProposedText);
    }

    [Fact]
    public async Task Ai_ImportRepair_AndScriptRefactoring_ProduceValidDiffProposals()
    {
        var enabledConfig = new AiConfiguration(
            IsEnabled: true,
            ProviderKind: AiProviderKind.CustomCompatible,
            EndpointUrl: "https://custom.ai.host/v1/chat/completions"
        );

        string originalMalformed = "curl -X POST https://api.example.com -H 'Content-Type: application/json' -d '{ missing_bracket: 123 '";
        string repairedContent = "curl -X POST https://api.example.com -H 'Content-Type: application/json' -d '{\"missing_bracket\": 123}'";

        var mockHandler = new MockAiHttpMessageHandler
        {
            MockResponseContent = $"{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":{JsonSerializer.Serialize(repairedContent)}}}}}]}}"
        };
        using var client = new HttpClient(mockHandler);
        var service = new AiAssistanceService(enabledConfig, client);

        // Import repair
        var repairProposal = await service.SuggestImportRepairAsync(originalMalformed, "SyntaxError: Unexpected token");
        Assert.Equal(AiProposalKind.ImportRepair, repairProposal.Kind);
        Assert.Equal(originalMalformed, repairProposal.OriginalText);
        Assert.Equal(repairedContent, repairProposal.ProposedText);
        Assert.Contains("--- a/imported-spec.json", repairProposal.UnifiedDiff);
        Assert.Contains("+++ b/imported-spec.json", repairProposal.UnifiedDiff);

        // Script refactoring
        string origScript = "function test() { pm.expect(1).to.eql(1); }";
        string refactoredScript = "assert.strictEqual(1, 1);";
        mockHandler.MockResponseContent = $"{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":{JsonSerializer.Serialize(refactoredScript)}}}}}]}}";

        var refactorProposal = await service.SuggestScriptRefactoringAsync(origScript, "Migrate from Postman pm.* to standard assert");
        Assert.Equal(AiProposalKind.ScriptRefactoring, refactorProposal.Kind);
        Assert.Equal(origScript, refactorProposal.OriginalText);
        Assert.Equal(refactoredScript, refactorProposal.ProposedText);
        Assert.Contains("--- a/script.js", refactorProposal.UnifiedDiff);
        Assert.Contains("+++ b/script.js", refactorProposal.UnifiedDiff);
    }
}

