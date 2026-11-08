namespace ApiWrangler.Domain;

public enum FlowNodeKind
{
    Request,
    Condition,
    Loop,
    Delay,
    Script,
    DatasetIterator,
    ParallelGroup,
    Join,
    Assertion,
    Subflow,
    Output
}

public sealed record FlowEdge(
    string Id,
    string SourceNodeId,
    string TargetNodeId,
    string? ConditionBranch = null // "true", "false", "item", "complete", "default"
);

public sealed record FlowNodeDefinition(
    string Id,
    FlowNodeKind Kind,
    string Name,
    IReadOnlyDictionary<string, string>? Config = null
);

public sealed record FlowDefinition(
    string Id,
    string Name,
    string StartNodeId,
    IReadOnlyList<FlowNodeDefinition> Nodes,
    IReadOnlyList<FlowEdge> Edges,
    IReadOnlyDictionary<string, string>? Variables = null
);

// Canvas coordinates stored separately from execution semantics to prevent Git conflicts
public sealed record FlowNodeLayout(
    string NodeId,
    double X,
    double Y,
    double Width = 160,
    double Height = 80,
    bool IsCollapsed = false
);

public sealed record FlowLayoutDefinition(
    string FlowId,
    IReadOnlyDictionary<string, FlowNodeLayout> NodeLayouts
);

public enum FlowExecutionOutcome
{
    Passed,
    Failed,
    Cancelled,
    TimedOut,
    Error
}

public sealed record FlowNodeExecutionRecord(
    string NodeId,
    string NodeName,
    FlowNodeKind Kind,
    FlowExecutionOutcome Outcome,
    double DurationMs,
    IReadOnlyDictionary<string, string> VariablesSnapshot, // Redacted
    string? ErrorMessage = null
);

public sealed record FlowExecutionReport(
    string FlowId,
    string FlowName,
    FlowExecutionOutcome Outcome,
    int TotalNodesExecuted,
    IReadOnlyList<FlowNodeExecutionRecord> NodeRecords,
    IReadOnlyDictionary<string, string> FinalVariables, // Redacted
    TimeSpan Duration,
    string? ErrorMessage = null
);

public sealed record FlowValidationDiagnostic(
    string? NodeId,
    string Severity, // "Error", "Warning"
    string Message
);

public sealed record FlowValidationResult(
    bool IsValid,
    IReadOnlyList<FlowValidationDiagnostic> Diagnostics
);

public enum DebuggerState
{
    Ready,
    Running,
    PausedAtBreakpoint,
    Completed,
    Cancelled
}

