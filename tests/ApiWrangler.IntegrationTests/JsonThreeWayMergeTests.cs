using System.Text.Json.Nodes;
using ApiWrangler.Execution;

namespace ApiWrangler.IntegrationTests;

public sealed class JsonThreeWayMergeTests
{
    [Fact]
    public void CreateProposal_MergesIndependentPropertiesAndPreservesUnknownFields()
    {
        var result = Merge("""{"known":1,"future":{"flag":true},"local":0,"incoming":0}""",
            """{"known":2,"future":{"flag":true},"local":0,"incoming":0}""",
            """{"known":1,"future":{"flag":true},"local":0,"incoming":3}""");

        Assert.True(result.IsConflictFree);
        Assert.Equal(2, result.Proposal["known"]!.GetValue<int>());
        Assert.Equal(3, result.Proposal["incoming"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"flag":true}"""), result.Proposal["future"]));
    }

    [Fact]
    public void CreateProposal_ReportsSameFieldConflictAndKeepsLocalProposal()
    {
        var result = Merge("""{"name":"base"}""", """{"name":"local"}""", """{"name":"incoming"}""");

        Assert.False(result.IsConflictFree);
        Assert.Equal("/name", Assert.Single(result.Conflicts).Path);
        Assert.Equal("local", result.Proposal["name"]!.GetValue<string>());
    }

    [Fact]
    public void CreateProposal_ReportsDeleteVersusEditConflict()
    {
        var result = Merge("""{"items":[{"id":"r1","value":"base"}]}""",
            """{"items":[]}""", """{"items":[{"id":"r1","value":"edited"}]}""");

        Assert.False(result.IsConflictFree);
        Assert.Contains(result.Conflicts, conflict => conflict.Path == "/items/id=r1" && conflict.Reason.Contains("deleted", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateProposal_ReportsDuplicateImmutableCaseIds()
    {
        var result = Merge("""{"rows":[{"_caseId":"case-1","value":"base"}]}""",
            """{"rows":[{"_caseId":"case-1","value":"local"},{"_caseId":"case-1","value":"duplicate"}]}""",
            """{"rows":[{"_caseId":"case-1","value":"base"}]}""");

        Assert.False(result.IsConflictFree);
        Assert.Equal("/rows/_caseId=case-1", Assert.Single(result.Conflicts).Path);
        Assert.Equal(2, ((JsonArray)result.Proposal["rows"]!).Count);
    }

    [Fact]
    public void CreateProposal_PreservesUnilateralRowReorderAndMergesIndependentCells()
    {
        var result = Merge("""{"rows":[{"_caseId":"a","value":"a0"},{"_caseId":"b","value":"b0"},{"_caseId":"c","value":"c0"}]}""",
            """{"rows":[{"_caseId":"b","value":"b1"},{"_caseId":"a","value":"a0"},{"_caseId":"c","value":"c0"}]}""",
            """{"rows":[{"_caseId":"a","value":"a0"},{"_caseId":"b","value":"b0"},{"_caseId":"c","value":"c1"}]}""");

        Assert.True(result.IsConflictFree);
        var rows = (JsonArray)result.Proposal["rows"]!;
        Assert.Equal(new[] { "b", "a", "c" }, rows.Select(row => row!["_caseId"]!.GetValue<string>()));
        Assert.Equal("b1", rows[0]!["value"]!.GetValue<string>());
        Assert.Equal("c1", rows[2]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void CreateProposal_ReportsIncompatibleTwoSidedRowReorders()
    {
        var result = Merge("""{"rows":[{"_caseId":"a"},{"_caseId":"b"},{"_caseId":"c"}]}""",
            """{"rows":[{"_caseId":"b"},{"_caseId":"a"},{"_caseId":"c"}]}""",
            """{"rows":[{"_caseId":"a"},{"_caseId":"c"},{"_caseId":"b"}]}""");

        Assert.False(result.IsConflictFree);
        Assert.Contains(result.Conflicts, conflict => conflict.Path == "/rows" && conflict.Reason.Contains("reordered", StringComparison.Ordinal));
    }

    private static JsonThreeWayMergeResult Merge(string @base, string local, string incoming) =>
        JsonThreeWayMerge.CreateProposal(JsonNode.Parse(@base)!, JsonNode.Parse(local)!, JsonNode.Parse(incoming)!);
}
