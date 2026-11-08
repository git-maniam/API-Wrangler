using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class VisualFlowTests
{
    [Fact]
    public void VisualFlow_Validation_RejectsUnreachableNodesAndUnboundedLoops()
    {
        var engine = new VisualFlowEngine();

        // 1. Flow with unreachable node
        var unreachableFlow = new FlowDefinition(
            Id: "flow-unreachable",
            Name: "Unreachable Test Flow",
            StartNodeId: "node-start",
            Nodes: new List<FlowNodeDefinition>
            {
                new FlowNodeDefinition("node-start", FlowNodeKind.Output, "Start Node"),
                new FlowNodeDefinition("node-orphan", FlowNodeKind.Output, "Orphan Node")
            },
            Edges: new List<FlowEdge>() // No edge from start to orphan
        );

        var result1 = engine.ValidateFlow(unreachableFlow);
        Assert.False(result1.IsValid);
        Assert.Contains(result1.Diagnostics, d => d.Message.Contains("Unreachable node") && d.NodeId == "node-orphan");

        // 2. Flow with unbounded loop (missing MaxIterations)
        var unboundedLoopFlow = new FlowDefinition(
            Id: "flow-unbounded",
            Name: "Unbounded Loop Flow",
            StartNodeId: "loop-1",
            Nodes: new List<FlowNodeDefinition>
            {
                new FlowNodeDefinition(
                    Id: "loop-1",
                    Kind: FlowNodeKind.Loop,
                    Name: "Infinite Loop",
                    Config: new Dictionary<string, string> { ["Condition"] = "true" } // missing MaxIterations!
                )
            },
            Edges: new List<FlowEdge>()
        );

        var result2 = engine.ValidateFlow(unboundedLoopFlow);
        Assert.False(result2.IsValid);
        Assert.Contains(result2.Diagnostics, d => d.Message.Contains("Unbounded loop detected") && d.NodeId == "loop-1");

        // 3. Valid flow with bounded loop passes
        var validFlow = new FlowDefinition(
            Id: "flow-valid",
            Name: "Valid Bounded Flow",
            StartNodeId: "loop-bounded",
            Nodes: new List<FlowNodeDefinition>
            {
                new FlowNodeDefinition(
                    Id: "loop-bounded",
                    Kind: FlowNodeKind.Loop,
                    Name: "Bounded Loop",
                    Config: new Dictionary<string, string> { ["MaxIterations"] = "5" }
                ),
                new FlowNodeDefinition(
                    Id: "node-end",
                    Kind: FlowNodeKind.Output,
                    Name: "End Node"
                )
            },
            Edges: new List<FlowEdge>
            {
                new FlowEdge("e1", "loop-bounded", "node-end", "complete")
            }
        );

        var result3 = engine.ValidateFlow(validFlow);
        Assert.True(result3.IsValid);
        Assert.DoesNotContain(result3.Diagnostics, d => d.Severity == "Error");
    }

    [Fact]
    public async Task VisualFlow_Cancellation_PropagatesImmediately()
    {
        var engine = new VisualFlowEngine();

        var flow = new FlowDefinition(
            Id: "flow-cancellation",
            Name: "Cancellation Flow",
            StartNodeId: "delay-1",
            Nodes: new List<FlowNodeDefinition>
            {
                new FlowNodeDefinition(
                    Id: "delay-1",
                    Kind: FlowNodeKind.Delay,
                    Name: "Long Delay",
                    Config: new Dictionary<string, string> { ["DelayMs"] = "30000" }
                ),
                new FlowNodeDefinition("node-after", FlowNodeKind.Output, "After Delay")
            },
            Edges: new List<FlowEdge>
            {
                new FlowEdge("e1", "delay-1", "node-after")
            }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var report = await engine.ExecuteFlowAsync(flow, cancellationToken: cts.Token);

        Assert.Equal(FlowExecutionOutcome.Cancelled, report.Outcome);
        Assert.NotNull(report.ErrorMessage);
        Assert.Contains("cancelled", report.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualFlow_CanvasCoordinates_SeparatedFromSemantics()
    {
        // 1. Semantic flow definition has zero coordinate properties
        var flow = new FlowDefinition(
            Id: "checkout-flow",
            Name: "Checkout Visual Flow",
            StartNodeId: "node-init",
            Nodes: new List<FlowNodeDefinition>
            {
                new FlowNodeDefinition("node-init", FlowNodeKind.Request, "Init Cart"),
                new FlowNodeDefinition("node-pay", FlowNodeKind.Request, "Process Payment")
            },
            Edges: new List<FlowEdge>
            {
                new FlowEdge("e1", "node-init", "node-pay")
            }
        );

        string semanticJson = JsonSerializer.Serialize(flow);
        Assert.DoesNotContain("\"X\":", semanticJson);
        Assert.DoesNotContain("\"Y\":", semanticJson);

        // 2. Separate canvas layout definition
        var layout = new FlowLayoutDefinition(
            FlowId: "checkout-flow",
            NodeLayouts: new Dictionary<string, FlowNodeLayout>
            {
                ["node-init"] = new FlowNodeLayout(NodeId: "node-init", X: 100.0, Y: 200.0, Width: 180, Height: 90),
                ["node-pay"] = new FlowNodeLayout(NodeId: "node-pay", X: 350.0, Y: 200.0, Width: 180, Height: 90)
            }
        );

        string layoutJson = JsonSerializer.Serialize(layout);
        Assert.Contains("\"X\":", layoutJson);
        Assert.Contains("\"Y\":", layoutJson);

        // Mutating canvas position (e.g. moving a node) mutates ONLY the layout, not semantic definition
        var updatedLayout = layout with
        {
            NodeLayouts = new Dictionary<string, FlowNodeLayout>(layout.NodeLayouts)
            {
                ["node-init"] = layout.NodeLayouts["node-init"] with { X = 150.0, Y = 250.0 }
            }
        };

        // Semantic hash remains completely untouched
        string unchangedSemanticJson = JsonSerializer.Serialize(flow);
        Assert.Equal(semanticJson, unchangedSemanticJson);
        Assert.Equal(150.0, updatedLayout.NodeLayouts["node-init"].X);
    }

    [Fact]
    public async Task VisualFlow_SharedExecutionEngine_AndSecretRedaction()
    {
        var engine = new VisualFlowEngine();

        var flow = new FlowDefinition(
            Id: "auth-data-flow",
            Name: "Authenticated Data Flow",
            StartNodeId: "node-1",
            Nodes: new List<FlowNodeDefinition>
            {
                new FlowNodeDefinition(
                    Id: "node-1",
                    Kind: FlowNodeKind.Output,
                    Name: "Set Context",
                    Config: new Dictionary<string, string>
                    {
                        ["VariableName"] = "userId",
                        ["Value"] = "user_42"
                    }
                ),
                new FlowNodeDefinition(
                    Id: "node-2",
                    Kind: FlowNodeKind.Assertion,
                    Name: "Verify User",
                    Config: new Dictionary<string, string>
                    {
                        ["Target"] = "{{userId}}",
                        ["Operator"] = "Equals",
                        ["Expected"] = "user_42"
                    }
                )
            },
            Edges: new List<FlowEdge>
            {
                new FlowEdge("e1", "node-1", "node-2")
            }
        );

        var initialVars = new Dictionary<string, string>
        {
            ["db_password"] = "super-secret-password-999",
            ["api_token"] = "awr_tok_live_1234567890",
            ["app_name"] = "Wrangler"
        };

        var report = await engine.ExecuteFlowAsync(flow, initialVariables: initialVars);

        Assert.Equal(FlowExecutionOutcome.Passed, report.Outcome);
        Assert.Equal(2, report.TotalNodesExecuted);

        // Secret values are completely redacted in final variables and snapshots
        Assert.Equal("[REDACTED]", report.FinalVariables["db_password"]);
        Assert.Equal("[REDACTED]", report.FinalVariables["api_token"]);
        Assert.Equal("Wrangler", report.FinalVariables["app_name"]);
        Assert.Equal("user_42", report.FinalVariables["userId"]);
    }

    [Fact]
    public async Task VisualFlow_DebuggerSession_SteppingAndBreakpoints()
    {
        var engine = new VisualFlowEngine();

        var flow = new FlowDefinition(
            Id: "debug-flow",
            Name: "Debug Flow",
            StartNodeId: "step-1",
            Nodes: new List<FlowNodeDefinition>
            {
                new FlowNodeDefinition("step-1", FlowNodeKind.Output, "Step One", new Dictionary<string, string> { ["VariableName"] = "a", ["Value"] = "1" }),
                new FlowNodeDefinition("step-2", FlowNodeKind.Output, "Step Two", new Dictionary<string, string> { ["VariableName"] = "b", ["Value"] = "2" }),
                new FlowNodeDefinition("step-3", FlowNodeKind.Output, "Step Three", new Dictionary<string, string> { ["VariableName"] = "c", ["Value"] = "3" })
            },
            Edges: new List<FlowEdge>
            {
                new FlowEdge("e1", "step-1", "step-2"),
                new FlowEdge("e2", "step-2", "step-3")
            }
        );

        var session = engine.CreateDebugSession(flow);
        Assert.Equal(DebuggerState.Ready, session.State);
        Assert.Equal("step-1", session.CurrentNodeId);

        // Set breakpoint on step-2
        session.SetBreakpoint("step-2");

        // Continue to breakpoint
        var records = await session.ContinueAsync();
        Assert.Single(records);
        Assert.Equal("step-1", records[0].NodeId);
        Assert.Equal(DebuggerState.PausedAtBreakpoint, session.State);
        Assert.Equal("step-2", session.CurrentNodeId);

        // Step over step-2
        var stepRecord = await session.StepAsync();
        Assert.NotNull(stepRecord);
        Assert.Equal("step-2", stepRecord.NodeId);
        Assert.Equal("step-3", session.CurrentNodeId);

        // Finish execution
        await session.ContinueAsync();
        Assert.Equal(DebuggerState.Completed, session.State);
        Assert.Null(session.CurrentNodeId);
    }
}

