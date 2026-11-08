namespace ApiWrangler.Domain;

public sealed record ContractBreakingChange(
    string Category, // "RemovedEndpoint", "RemovedMethod", "ChangedParameter", "NewRequiredField", "RemovedResponseField"
    string Severity, // "Breaking", "NonBreaking", "Deprecation"
    string Path,
    string? Method,
    string Description
);

public sealed record ContractDiffReport(
    string BaselineTitle,
    string TargetTitle,
    int TotalBreakingChanges,
    int TotalNonBreakingChanges,
    IReadOnlyList<ContractBreakingChange> Changes
);

public sealed record ContractOperationLink(
    Guid RequestId,
    string OperationId,
    string Path,
    string Method,
    string? ContractPath = null
);

public sealed record CatalogSearchResult(
    Guid RequestId,
    string Name,
    string Method,
    string Url,
    string? CollectionName,
    double Score,
    string MatchedField
);

public enum SdkGenerationTarget
{
    CSharp,
    TypeScript,
    Python
}

public sealed record GeneratedSdkFile(
    string RelativePath,
    string Content
);

public sealed record SdkGenerationResult(
    SdkGenerationTarget Target,
    string PreviewDirectory,
    IReadOnlyList<GeneratedSdkFile> Files
);

