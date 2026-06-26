using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.Integration.Tests;

internal sealed class ImmediateEndTurnLlmHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    content = new[] { new { type = "text", text = "ok" } },
                    stop_reason = "end_turn"
                }),
                Encoding.UTF8,
                "application/json")
        });
}

/// <summary>
/// Fake LLM handler for Phase 4 slice 11b fan-out: orchestrator enqueues planner,
/// planner records plan artifact, orchestrator re-invoked for fan-out, parallel workers execute slices.
/// </summary>
internal sealed class FanOutLlmHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _orchestratorInvocations = new();
    private readonly bool _includeDependentSlice;

    public FanOutLlmHandler(bool includeDependentSlice = false) =>
        _includeDependentSlice = includeDependentSlice;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        var step = (messages.GetArrayLength() - 1) / 2;
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";

        var json = firstMsg.StartsWith("Begin orchestrating")
            ? OrchestratorStep(step, firstMsg, messages)
            : firstMsg.StartsWith("Plan work unit")
                ? PlannerStep(step, firstMsg, messages)
                : WorkerStep(step, firstMsg, messages);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private string OrchestratorStep(int step, string firstMsg, JsonElement messages)
    {
        var wuId = ParseBetween(firstMsg, "Begin orchestrating work unit ", ". Your agent ID is");
        var invocation = step == 0
            ? _orchestratorInvocations.AddOrUpdate(wuId, 1, (_, n) => n + 1)
            : _orchestratorInvocations.GetOrAdd(wuId, 1);

        return invocation == 1
            ? FirstOrchestratorInvocation(step, wuId)
            : LaterOrchestratorInvocation(step, wuId);
    }

    private static string FirstOrchestratorInvocation(int step, string wuId) => step switch
    {
        0 => ToolUse("tu-o-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
        1 => ToolUse("tu-o-2", "nm_v1_scheduler_enqueue", new { workUnitId = wuId, profileId = "planner" }),
        _ => EndTurn(),
    };

    private static string LaterOrchestratorInvocation(int step, string wuId) => step switch
    {
        0 => ToolUse("tu-o-r1", "nm_v1_projection_get", new { projectionType = "AgentWorkspace", workUnitId = wuId }),
        _ => EndTurn(),
    };

    private string PlannerStep(int step, string firstMsg, JsonElement messages)
    {
        var wuId = ParseBetween(firstMsg, "Plan work unit ", ". Your agent ID is");
        var branchId = step >= 1 ? ExtractFromToolResult(messages, "BranchId") ?? ExtractFromToolResult(messages, "branchId") : null;

        var slices = _includeDependentSlice
            ? new object[]
            {
                new
                {
                    sliceId = "s1",
                    goal = "Implement Foo.cs",
                    fileScope = new[] { "src/Foo.cs" },
                    dependsOn = Array.Empty<string>(),
                    steps = new[] { "Create Foo.cs" }
                },
                new
                {
                    sliceId = "s2",
                    goal = "Add Bar.cs referencing Foo",
                    fileScope = new[] { "src/Bar.cs" },
                    dependsOn = new[] { "s1" },
                    steps = new[] { "Create Bar.cs" }
                }
            }
            : new object[]
            {
                new
                {
                    sliceId = "s1",
                    goal = "Implement Foo.cs",
                    fileScope = new[] { "src/Foo.cs" },
                    dependsOn = Array.Empty<string>(),
                    steps = new[] { "Create Foo.cs" }
                },
                new
                {
                    sliceId = "s2",
                    goal = "Add Bar.cs",
                    fileScope = new[] { "src/Bar.cs" },
                    dependsOn = Array.Empty<string>(),
                    steps = new[] { "Create Bar.cs" }
                }
            };

        var planJson = JsonSerializer.Serialize(new { slices });

        return step switch
        {
            0 => ToolUse("tu-p-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
             1 => ToolUse("tu-p-2", McpToolNames.ArtifactRecordPlan, new
            {
                workUnitId = wuId,
                planContent = planJson
            }),
            _ => EndTurn(),
        };
    }

    private static string WorkerStep(int step, string firstMsg, JsonElement messages)
    {
        var taskId = ParseBetween(firstMsg, "Execute task ", " for work unit ");
        var wuId   = ParseBetween(firstMsg, "for work unit ", ". Your agent ID is");
        var agentId = ParseBetween(firstMsg, "Your agent ID is ", ".");

        var goal = step >= 1
            ? ExtractFromToolResult(messages, "Goal") ?? ExtractFromToolResult(messages, "goal") ?? ""
            : "";
        var branchId = step >= 1
            ? ExtractFromToolResult(messages, "BranchId") ?? ExtractFromToolResult(messages, "branchId")
            : null;

        var targetFile = goal.Contains("Foo", StringComparison.OrdinalIgnoreCase) ? "src/Foo.cs"
            : goal.Contains("Bar", StringComparison.OrdinalIgnoreCase) ? "src/Bar.cs"
            : "src/Unknown.cs";

        var fileContent = targetFile.Contains("Foo") ? "class Foo {}" : "class Bar { Foo f; }";

        return step switch
        {
            0 => ToolUse("tu-w-1", "nm_v1_task_update", new { taskId, status = "InProgress" }),
            1 => ToolUse("tu-w-2", "nm_v1_workunit_get", new { workUnitId = wuId }),
            2 => ToolUse("tu-w-3", "nm_v1_workspace_write", new { branchId, path = targetFile, content = fileContent }),
            3 => ToolUse("tu-w-4", "nm_v1_task_update", new { taskId, status = "Completed" }),
            4 => ToolUse("tu-w-5", "nm_v1_merge_propose", new
            {
                sourceBranch = branchId ?? "unknown-branch",
                targetBranch = "main",
                summary = $"Completed {goal}",
                workUnitId = wuId,
                agentId
            }),
            5 => ToolUse("tu-w-6", "nm_v1_merge_validate", new
            {
                proposalId = ExtractFromToolResult(messages, "proposalId") ?? "unknown"
            }),
            _ => EndTurn(),
        };
    }

    private static string? ExtractFromToolResult(JsonElement messages, string key)
    {
        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (!msg.TryGetProperty("role", out var role) || role.GetString() != "user") continue;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "tool_result") continue;
                if (!item.TryGetProperty("content", out var toolContent)) continue;
                var s = toolContent.GetString();
                if (s is null) continue;
                try
                {
                    using var d = JsonDocument.Parse(s);
                    if (d.RootElement.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                        return val.GetString();
                }
                catch { /* skip malformed */ }
            }
        }
        return null;
    }

    private static string ToolUse(string id, string name, object input) =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "tool_use", id, name, input } },
            stop_reason = "tool_use"
        });

    private static string EndTurn() =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Done." } },
            stop_reason = "end_turn"
        });

    private static string ParseBetween(string s, string start, string end)
    {
        var si = s.IndexOf(start, StringComparison.Ordinal);
        if (si < 0) return "";
        si += start.Length;
        var ei = s.IndexOf(end, si, StringComparison.Ordinal);
        return ei < 0 ? s[si..] : s[si..ei];
    }
}
