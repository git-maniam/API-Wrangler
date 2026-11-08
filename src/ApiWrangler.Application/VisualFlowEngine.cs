using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class VisualFlowEngine
{
    private readonly HttpRequestService? httpService;

    private static readonly HashSet<string> SensitiveKeySubstrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "secret",
        "token",
        "apikey",
        "api_key",
        "credential",
        "auth",
        "privatekey"
    };

    public VisualFlowEngine(HttpRequestService? httpService = null)
    {
        this.httpService = httpService;
    }

    public FlowValidationResult ValidateFlow(
        FlowDefinition flow,
        IReadOnlyDictionary<string, FlowDefinition>? availableSubflows = null)
    {
        var diagnostics = new List<FlowValidationDiagnostic>();

        if (string.IsNullOrWhiteSpace(flow.StartNodeId))
        {
            diagnostics.Add(new FlowValidationDiagnostic(null, "Error", "Flow must declare a valid StartNodeId."));
            return new FlowValidationResult(false, diagnostics);
        }

        var nodeMap = new Dictionary<string, FlowNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in flow.Nodes)
        {
            if (nodeMap.ContainsKey(node.Id))
            {
                diagnostics.Add(new FlowValidationDiagnostic(node.Id, "Error", $"Duplicate node ID detected: '{node.Id}'."));
            }
            else
            {
                nodeMap[node.Id] = node;
            }
        }

        if (!nodeMap.ContainsKey(flow.StartNodeId))
        {
            diagnostics.Add(new FlowValidationDiagnostic(flow.StartNodeId, "Error", $"StartNodeId '{flow.StartNodeId}' does not exist in flow nodes."));
        }

        // Validate edges
        var outgoingEdges = new Dictionary<string, List<FlowEdge>>(StringComparer.Ordinal);
        var incomingEdges = new Dictionary<string, List<FlowEdge>>(StringComparer.Ordinal);
        foreach (var n in flow.Nodes)
        {
            outgoingEdges[n.Id] = new List<FlowEdge>();
            incomingEdges[n.Id] = new List<FlowEdge>();
        }

        foreach (var edge in flow.Edges)
        {
            if (!nodeMap.ContainsKey(edge.SourceNodeId))
            {
                diagnostics.Add(new FlowValidationDiagnostic(null, "Error", $"Edge '{edge.Id}' references nonexistent source node '{edge.SourceNodeId}'."));
            }
            if (!nodeMap.ContainsKey(edge.TargetNodeId))
            {
                diagnostics.Add(new FlowValidationDiagnostic(null, "Error", $"Edge '{edge.Id}' references nonexistent target node '{edge.TargetNodeId}'."));
            }

            if (outgoingEdges.TryGetValue(edge.SourceNodeId, out var outList))
            {
                outList.Add(edge);
            }
            if (incomingEdges.TryGetValue(edge.TargetNodeId, out var inList))
            {
                inList.Add(edge);
            }
        }

        // 1. Detect unreachable nodes
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        if (nodeMap.ContainsKey(flow.StartNodeId))
        {
            var queue = new Queue<string>();
            queue.Enqueue(flow.StartNodeId);
            reachable.Add(flow.StartNodeId);

            while (queue.Count > 0)
            {
                string curr = queue.Dequeue();
                if (outgoingEdges.TryGetValue(curr, out var edges))
                {
                    foreach (var e in edges)
                    {
                        if (reachable.Add(e.TargetNodeId))
                        {
                            queue.Enqueue(e.TargetNodeId);
                        }
                    }
                }
            }
        }

        foreach (var node in flow.Nodes)
        {
            if (!reachable.Contains(node.Id))
            {
                diagnostics.Add(new FlowValidationDiagnostic(
                    node.Id,
                    "Error",
                    $"Unreachable node '{node.Name}' ({node.Id}) detected. All nodes must be reachable from the Start node."
                ));
            }
        }

        // 2. Detect unbounded loops
        foreach (var node in flow.Nodes)
        {
            if (node.Kind == FlowNodeKind.Loop)
            {
                bool hasBound = false;
                if (node.Config is not null && node.Config.TryGetValue("MaxIterations", out var maxStr))
                {
                    if (int.TryParse(maxStr, out int max) && max > 0 && max <= 100_000)
                    {
                        hasBound = true;
                    }
                }

                if (!hasBound)
                {
                    diagnostics.Add(new FlowValidationDiagnostic(
                        node.Id,
                        "Error",
                        $"Unbounded loop detected on node '{node.Name}' ({node.Id}). Loops must specify an explicit positive MaxIterations bound."
                    ));
                }
            }

            // 3. Subflow validation
            if (node.Kind == FlowNodeKind.Subflow)
            {
                string? subflowId = node.Config != null && node.Config.TryGetValue("SubflowId", out var sf) ? sf : null;
                if (string.IsNullOrEmpty(subflowId) || availableSubflows == null || !availableSubflows.ContainsKey(subflowId))
                {
                    diagnostics.Add(new FlowValidationDiagnostic(
                        node.Id,
                        "Error",
                        $"Subflow reference '{subflowId ?? "null"}' on node '{node.Name}' was not found in available subflows."
                    ));
                }
                else if (string.Equals(subflowId, flow.Id, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new FlowValidationDiagnostic(
                        node.Id,
                        "Error",
                        $"Direct recursive subflow reference to '{flow.Id}' is not permitted."
                    ));
                }
            }
        }

        bool isValid = diagnostics.All(d => d.Severity != "Error");
        return new FlowValidationResult(isValid, diagnostics);
    }

    public static IReadOnlyDictionary<string, string> RedactVariables(IReadOnlyDictionary<string, string> variables)
    {
        var redacted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in variables)
        {
            bool isSensitive = SensitiveKeySubstrings.Any(s => k.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                               v.StartsWith("awr_tok_", StringComparison.OrdinalIgnoreCase) ||
                               v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

            redacted[k] = isSensitive ? "[REDACTED]" : v;
        }
        return redacted;
    }

    public async Task<FlowExecutionReport> ExecuteFlowAsync(
        FlowDefinition flow,
        IReadOnlyDictionary<string, string>? initialVariables = null,
        IReadOnlyDictionary<string, FlowDefinition>? availableSubflows = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateFlow(flow, availableSubflows);
        if (!validation.IsValid)
        {
            var firstErr = validation.Diagnostics.FirstOrDefault(d => d.Severity == "Error")?.Message ?? "Validation failed.";
            return new FlowExecutionReport(
                FlowId: flow.Id,
                FlowName: flow.Name,
                Outcome: FlowExecutionOutcome.Error,
                TotalNodesExecuted: 0,
                NodeRecords: Array.Empty<FlowNodeExecutionRecord>(),
                FinalVariables: RedactVariables(initialVariables ?? new Dictionary<string, string>()),
                Duration: TimeSpan.Zero,
                ErrorMessage: $"Flow validation failed: {firstErr}"
            );
        }

        var sw = Stopwatch.StartNew();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (flow.Variables is not null)
        {
            foreach (var (k, v) in flow.Variables) variables[k] = v;
        }
        if (initialVariables is not null)
        {
            foreach (var (k, v) in initialVariables) variables[k] = v;
        }

        var nodeMap = flow.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var outgoingEdges = flow.Edges.GroupBy(e => e.SourceNodeId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var loopCounters = new Dictionary<string, int>(StringComparer.Ordinal);

        var nodeRecords = new List<FlowNodeExecutionRecord>();
        string? currentNodeId = flow.StartNodeId;
        FlowExecutionOutcome flowOutcome = FlowExecutionOutcome.Passed;
        string? errorMessage = null;

        while (!string.IsNullOrEmpty(currentNodeId) && nodeMap.TryGetValue(currentNodeId, out var node))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                flowOutcome = FlowExecutionOutcome.Cancelled;
                errorMessage = "Flow execution cancelled.";
                break;
            }

            var nodeWatch = Stopwatch.StartNew();
            FlowExecutionOutcome nodeOutcome = FlowExecutionOutcome.Passed;
            string? nodeError = null;
            string? nextNodeId = null;

            try
            {
                switch (node.Kind)
                {
                    case FlowNodeKind.Request:
                        nodeOutcome = await ExecuteRequestNodeAsync(node, variables, cancellationToken).ConfigureAwait(false);
                        nextNodeId = GetDefaultNextNode(outgoingEdges, node.Id);
                        break;

                    case FlowNodeKind.Condition:
                        bool conditionResult = EvaluateConditionNode(node, variables);
                        string branch = conditionResult ? "true" : "false";
                        nextNodeId = GetBranchNextNode(outgoingEdges, node.Id, branch) ?? GetDefaultNextNode(outgoingEdges, node.Id);
                        break;

                    case FlowNodeKind.Loop:
                        int maxIterations = 10;
                        if (node.Config != null && node.Config.TryGetValue("MaxIterations", out var miStr) && int.TryParse(miStr, out int mi))
                        {
                            maxIterations = mi;
                        }

                        int currentIter = loopCounters.TryGetValue(node.Id, out int it) ? it : 0;
                        if (currentIter < maxIterations)
                        {
                            loopCounters[node.Id] = currentIter + 1;
                            variables[$"{node.Id}_iteration"] = loopCounters[node.Id].ToString();
                            nextNodeId = GetBranchNextNode(outgoingEdges, node.Id, "item") ??
                                         GetBranchNextNode(outgoingEdges, node.Id, "body") ??
                                         GetDefaultNextNode(outgoingEdges, node.Id);
                        }
                        else
                        {
                            // Loop complete
                            nextNodeId = GetBranchNextNode(outgoingEdges, node.Id, "complete") ??
                                         GetBranchNextNode(outgoingEdges, node.Id, "next");
                        }
                        break;

                    case FlowNodeKind.Delay:
                        int delayMs = 100;
                        if (node.Config != null && node.Config.TryGetValue("DelayMs", out var dStr) && int.TryParse(dStr, out int d))
                        {
                            delayMs = d;
                        }
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        nextNodeId = GetDefaultNextNode(outgoingEdges, node.Id);
                        break;

                    case FlowNodeKind.Assertion:
                        bool assertOk = EvaluateAssertionNode(node, variables, out string failReason);
                        if (!assertOk)
                        {
                            nodeOutcome = FlowExecutionOutcome.Failed;
                            nodeError = failReason;
                            flowOutcome = FlowExecutionOutcome.Failed;
                            errorMessage = $"Assertion failed on node '{node.Name}': {failReason}";
                        }
                        nextNodeId = GetDefaultNextNode(outgoingEdges, node.Id);
                        break;

                    case FlowNodeKind.Output:
                        if (node.Config != null && node.Config.TryGetValue("VariableName", out var outVar))
                        {
                            string outVal = node.Config.TryGetValue("Value", out var val) ? ResolveTemplate(val, variables) : "";
                            variables[outVar] = outVal;
                        }
                        nextNodeId = GetDefaultNextNode(outgoingEdges, node.Id);
                        break;

                    case FlowNodeKind.Subflow:
                        if (node.Config != null && node.Config.TryGetValue("SubflowId", out var sfId) &&
                            availableSubflows != null && availableSubflows.TryGetValue(sfId, out var subflowDef))
                        {
                            var subReport = await ExecuteFlowAsync(subflowDef, variables, availableSubflows, cancellationToken).ConfigureAwait(false);
                            if (subReport.Outcome != FlowExecutionOutcome.Passed)
                            {
                                nodeOutcome = subReport.Outcome;
                                nodeError = subReport.ErrorMessage;
                                flowOutcome = subReport.Outcome;
                            }
                        }
                        nextNodeId = GetDefaultNextNode(outgoingEdges, node.Id);
                        break;

                    default:
                        // Script, ParallelGroup, Join, etc.
                        if (node.Config != null && node.Config.TryGetValue("SetVariable", out var svName) &&
                            node.Config.TryGetValue("SetValue", out var svVal))
                        {
                            variables[svName] = ResolveTemplate(svVal, variables);
                        }
                        nextNodeId = GetDefaultNextNode(outgoingEdges, node.Id);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                nodeOutcome = FlowExecutionOutcome.Cancelled;
                flowOutcome = FlowExecutionOutcome.Cancelled;
                nodeError = "Operation cancelled.";
                errorMessage = "Execution cancelled.";
                break;
            }
            catch (Exception ex)
            {
                nodeOutcome = FlowExecutionOutcome.Error;
                flowOutcome = FlowExecutionOutcome.Error;
                nodeError = ex.Message;
                errorMessage = $"Error in node '{node.Name}': {ex.Message}";
                break;
            }

            nodeWatch.Stop();
            nodeRecords.Add(new FlowNodeExecutionRecord(
                NodeId: node.Id,
                NodeName: node.Name,
                Kind: node.Kind,
                Outcome: nodeOutcome,
                DurationMs: nodeWatch.Elapsed.TotalMilliseconds,
                VariablesSnapshot: RedactVariables(variables),
                ErrorMessage: nodeError
            ));

            if (nodeOutcome == FlowExecutionOutcome.Failed || nodeOutcome == FlowExecutionOutcome.Error)
            {
                break;
            }

            currentNodeId = nextNodeId;
        }

        sw.Stop();
        return new FlowExecutionReport(
            FlowId: flow.Id,
            FlowName: flow.Name,
            Outcome: flowOutcome,
            TotalNodesExecuted: nodeRecords.Count,
            NodeRecords: nodeRecords,
            FinalVariables: RedactVariables(variables),
            Duration: sw.Elapsed,
            ErrorMessage: errorMessage
        );
    }

    private async Task<FlowExecutionOutcome> ExecuteRequestNodeAsync(
        FlowNodeDefinition node,
        Dictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        string method = node.Config != null && node.Config.TryGetValue("Method", out var m) ? m : "GET";
        string url = node.Config != null && node.Config.TryGetValue("Url", out var u) ? ResolveTemplate(u, variables) : "http://localhost:5000/api";
        string? body = node.Config != null && node.Config.TryGetValue("Body", out var b) ? ResolveTemplate(b, variables) : null;

        var reqDef = new HttpRequestDefinition
        {
            Id = Guid.NewGuid(),
            Name = node.Name,
            Method = method,
            Url = url,
            Body = body
        };

        if (httpService is not null)
        {
            var res = await httpService.SendAsync(reqDef, cancellationToken: cancellationToken).ConfigureAwait(false);
            variables[$"{node.Id}_status"] = (res.StatusCode ?? 0).ToString();
            if (!string.IsNullOrEmpty(res.BodyPath) && File.Exists(res.BodyPath))
            {
                string resBody = await File.ReadAllTextAsync(res.BodyPath, cancellationToken).ConfigureAwait(false);
                variables[$"{node.Id}_response"] = resBody;
            }
            return res.Outcome == ExecutionOutcome.Passed ? FlowExecutionOutcome.Passed : FlowExecutionOutcome.Failed;
        }
        else
        {
            // Headless mock or mock execution when httpService not injected
            variables[$"{node.Id}_status"] = "200";
            variables[$"{node.Id}_response"] = "{\"status\": \"ok\"}";
            return FlowExecutionOutcome.Passed;
        }
    }

    private static bool EvaluateConditionNode(FlowNodeDefinition node, Dictionary<string, string> variables)
    {
        if (node.Config == null || !node.Config.TryGetValue("Expression", out var expr))
        {
            return true;
        }

        string resolved = ResolveTemplate(expr, variables).Trim();

        // Support simple equality: "a == b", "a != b", "a > b", "a < b"
        if (resolved.Contains("=="))
        {
            var parts = resolved.Split("==", 2);
            return string.Equals(parts[0].Trim(), parts[1].Trim(), StringComparison.OrdinalIgnoreCase);
        }
        if (resolved.Contains("!="))
        {
            var parts = resolved.Split("!=", 2);
            return !string.Equals(parts[0].Trim(), parts[1].Trim(), StringComparison.OrdinalIgnoreCase);
        }
        if (resolved.Contains(">"))
        {
            var parts = resolved.Split(">", 2);
            if (double.TryParse(parts[0].Trim(), out double l) && double.TryParse(parts[1].Trim(), out double r))
            {
                return l > r;
            }
        }
        if (resolved.Contains("<"))
        {
            var parts = resolved.Split("<", 2);
            if (double.TryParse(parts[0].Trim(), out double l) && double.TryParse(parts[1].Trim(), out double r))
            {
                return l < r;
            }
        }

        return !string.Equals(resolved, "false", StringComparison.OrdinalIgnoreCase) && !string.Equals(resolved, "0", StringComparison.Ordinal);
    }

    private static bool EvaluateAssertionNode(FlowNodeDefinition node, Dictionary<string, string> variables, out string failureReason)
    {
        failureReason = string.Empty;
        if (node.Config == null) return true;

        string target = node.Config.TryGetValue("Target", out var t) ? ResolveTemplate(t, variables) : "";
        string op = node.Config.TryGetValue("Operator", out var o) ? o : "Equals";
        string expected = node.Config.TryGetValue("Expected", out var e) ? ResolveTemplate(e, variables) : "";

        bool ok = op.ToLowerInvariant() switch
        {
            "equals" or "==" => string.Equals(target, expected, StringComparison.OrdinalIgnoreCase),
            "notequals" or "!=" => !string.Equals(target, expected, StringComparison.OrdinalIgnoreCase),
            "contains" => target.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "greaterthan" or ">" => double.TryParse(target, out double dt) && double.TryParse(expected, out double de) && dt > de,
            "lessthan" or "<" => double.TryParse(target, out double lt) && double.TryParse(expected, out double le) && lt < le,
            _ => string.Equals(target, expected, StringComparison.OrdinalIgnoreCase)
        };

        if (!ok)
        {
            failureReason = $"Expected '{target}' {op} '{expected}'.";
            return false;
        }

        return true;
    }

    private static string? GetDefaultNextNode(Dictionary<string, List<FlowEdge>> outgoingEdges, string nodeId)
    {
        if (outgoingEdges.TryGetValue(nodeId, out var edges) && edges.Count > 0)
        {
            var defaultEdge = edges.FirstOrDefault(e => string.IsNullOrEmpty(e.ConditionBranch) || e.ConditionBranch == "default");
            return defaultEdge != null ? defaultEdge.TargetNodeId : edges[0].TargetNodeId;
        }
        return null;
    }

    private static string? GetBranchNextNode(Dictionary<string, List<FlowEdge>> outgoingEdges, string nodeId, string branch)
    {
        if (outgoingEdges.TryGetValue(nodeId, out var edges))
        {
            var edge = edges.FirstOrDefault(e => string.Equals(e.ConditionBranch, branch, StringComparison.OrdinalIgnoreCase));
            return edge?.TargetNodeId;
        }
        return null;
    }

    private static string ResolveTemplate(string template, Dictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{")) return template;
        return Regex.Replace(template, @"\{\{([a-zA-Z0-9_\.]+)\}\}", match =>
        {
            string key = match.Groups[1].Value.Trim();
            return variables.TryGetValue(key, out var val) ? val : match.Value;
        });
    }

    public FlowDebugSession CreateDebugSession(
        FlowDefinition flow,
        IReadOnlyDictionary<string, string>? initialVariables = null,
        IReadOnlyDictionary<string, FlowDefinition>? availableSubflows = null)
    {
        return new FlowDebugSession(this, flow, initialVariables, availableSubflows);
    }
}

public sealed class FlowDebugSession
{
    private readonly VisualFlowEngine engine;
    private readonly FlowDefinition flow;
    private readonly IReadOnlyDictionary<string, FlowDefinition>? availableSubflows;
    private readonly Dictionary<string, string> variables;
    private readonly HashSet<string> breakpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlowNodeDefinition> nodeMap;
    private readonly Dictionary<string, List<FlowEdge>> outgoingEdges;
    private readonly Dictionary<string, int> loopCounters = new(StringComparer.Ordinal);

    private string? currentNodeId;
    private DebuggerState state = DebuggerState.Ready;

    public DebuggerState State => state;
    public string? CurrentNodeId => currentNodeId;
    public FlowNodeDefinition? CurrentNode => currentNodeId != null && nodeMap.TryGetValue(currentNodeId, out var n) ? n : null;

    public FlowDebugSession(
        VisualFlowEngine engine,
        FlowDefinition flow,
        IReadOnlyDictionary<string, string>? initialVariables = null,
        IReadOnlyDictionary<string, FlowDefinition>? availableSubflows = null)
    {
        this.engine = engine;
        this.flow = flow;
        this.availableSubflows = availableSubflows;

        variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (flow.Variables is not null)
        {
            foreach (var (k, v) in flow.Variables) variables[k] = v;
        }
        if (initialVariables is not null)
        {
            foreach (var (k, v) in initialVariables) variables[k] = v;
        }

        nodeMap = flow.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        outgoingEdges = flow.Edges.GroupBy(e => e.SourceNodeId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        currentNodeId = flow.StartNodeId;
    }

    public void SetBreakpoint(string nodeId) => breakpoints.Add(nodeId);
    public void RemoveBreakpoint(string nodeId) => breakpoints.Remove(nodeId);
    public bool HasBreakpoint(string nodeId) => breakpoints.Contains(nodeId);

    public IReadOnlyDictionary<string, string> InspectVariables()
    {
        return VisualFlowEngine.RedactVariables(variables);
    }

    public async Task<FlowNodeExecutionRecord?> StepAsync(CancellationToken cancellationToken = default)
    {
        if (state == DebuggerState.Completed || state == DebuggerState.Cancelled || currentNodeId == null)
        {
            return null;
        }

        if (!nodeMap.TryGetValue(currentNodeId, out var node))
        {
            state = DebuggerState.Completed;
            return null;
        }

        state = DebuggerState.Running;
        var nodeWatch = Stopwatch.StartNew();
        FlowExecutionOutcome outcome = FlowExecutionOutcome.Passed;
        string? error = null;
        string? nextNodeId = null;

        try
        {
            if (node.Kind == FlowNodeKind.Output && node.Config != null && node.Config.TryGetValue("VariableName", out var outVar))
            {
                variables[outVar] = node.Config.TryGetValue("Value", out var v) ? v : "";
            }
            else if (node.Kind == FlowNodeKind.Assertion && node.Config != null)
            {
                string target = node.Config.TryGetValue("Target", out var t) ? t : "";
                string expected = node.Config.TryGetValue("Expected", out var e) ? e : "";
                if (!string.Equals(target, expected, StringComparison.OrdinalIgnoreCase))
                {
                    outcome = FlowExecutionOutcome.Failed;
                    error = $"Assertion mismatch: {target} != {expected}";
                }
            }

            // Find next
            if (outgoingEdges.TryGetValue(node.Id, out var edges) && edges.Count > 0)
            {
                nextNodeId = edges[0].TargetNodeId;
            }
        }
        catch (Exception ex)
        {
            outcome = FlowExecutionOutcome.Error;
            error = ex.Message;
        }

        nodeWatch.Stop();
        var record = new FlowNodeExecutionRecord(
            NodeId: node.Id,
            NodeName: node.Name,
            Kind: node.Kind,
            Outcome: outcome,
            DurationMs: nodeWatch.Elapsed.TotalMilliseconds,
            VariablesSnapshot: InspectVariables(),
            ErrorMessage: error
        );

        currentNodeId = nextNodeId;
        if (currentNodeId == null)
        {
            state = DebuggerState.Completed;
        }
        else if (breakpoints.Contains(currentNodeId))
        {
            state = DebuggerState.PausedAtBreakpoint;
        }
        else
        {
            state = DebuggerState.Ready;
        }

        return record;
    }

    public async Task<IReadOnlyList<FlowNodeExecutionRecord>> ContinueAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<FlowNodeExecutionRecord>();
        while (state != DebuggerState.Completed && state != DebuggerState.Cancelled && currentNodeId != null)
        {
            var rec = await StepAsync(cancellationToken).ConfigureAwait(false);
            if (rec is not null) records.Add(rec);
            if (state == DebuggerState.PausedAtBreakpoint) break;
        }
        return records;
    }
}

