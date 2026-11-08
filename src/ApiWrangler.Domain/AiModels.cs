using System;
using System.Collections.Generic;

namespace ApiWrangler.Domain;

public enum AiProviderKind
{
    Disabled = 0,
    OpenAiCompatible = 1,
    LocalEndpoint = 2,
    CustomCompatible = 3
}

public sealed record AiConfiguration(
    bool IsEnabled = false,
    AiProviderKind ProviderKind = AiProviderKind.Disabled,
    string? EndpointUrl = null,
    string? ModelName = null,
    string? ApiKey = null,
    double Temperature = 0.2,
    int MaxTokens = 2048
);

public enum AiContextItemKind
{
    RequestUrl,
    RequestMethod,
    RequestHeaders,
    RequestBody,
    ResponseStatusCode,
    ResponseHeaders,
    ResponseBody,
    SchemaDefinition,
    ScriptCode,
    UserNotes
}

public sealed record AiContextItem(
    AiContextItemKind Kind,
    string Label,
    string Content,
    bool IsRedacted = false
);

public sealed record AiSanitizedContext(
    IReadOnlyList<AiContextItem> Items,
    IReadOnlyList<string> RedactionAuditLog
);

public enum AiProposalKind
{
    TestDraft,
    ResponseExplanation,
    DatasetGeneration,
    ImportRepair,
    ScriptRefactoring
}

public enum AiReviewStatus
{
    PendingReview,
    Approved,
    Rejected
}

public sealed class AiProposalDiff
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AiProposalKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
    public string OriginalText { get; init; } = string.Empty;
    public string ProposedText { get; init; } = string.Empty;
    public string UnifiedDiff { get; init; } = string.Empty;
    public AiReviewStatus Status { get; private set; } = AiReviewStatus.PendingReview;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Safety constraints mandated by Section 25
    public bool RequiresUserReview => true;
    public bool CanAutoExecute => false;
    public bool CanAutoCommit => false;

    public void Approve()
    {
        Status = AiReviewStatus.Approved;
    }

    public void Reject()
    {
        Status = AiReviewStatus.Rejected;
    }

    public void AttemptAutoExecute()
    {
        throw new InvalidOperationException("Generated code requires user review and cannot run automatically.");
    }

    public void AttemptAutoCommit()
    {
        throw new InvalidOperationException("Generated code requires user review and cannot commit automatically.");
    }
}

