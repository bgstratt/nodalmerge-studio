using System.Net;
using System.Text;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Fake LLM for the recursive-planning spike (plans/recursive-planning-spike.md S5/S5.b). Copied from
/// FanOutLlmHandler and extended for a 2-level tree:
///
///   root goal ("Assemble the widget product")
///     ├─ c1  (kind=compound) → sub-planner
///     │      ├─ g1 (kind=leaf)      → worker writes src/Alpha.cs
///     │      └─ g2 (kind=compound)  → DEMOTED to worker at the depth cap → writes src/Bravo.cs
///     └─ l1  (kind=leaf)            → worker writes src/Lima.cs   (proves a mixed tree)
///
/// The root planner and the c1 sub-planner are disambiguated by the goal text inlined in the planner
/// kickoff ("Goal: ..."). With MaxPlanDepth=2: c1 (a root slice, depth 1) sub-plans; its grandchildren
/// (depth 2) are forced to workers — g2, though marked compound, is demoted.
///
/// S5.b mode (peerContractMode=true) instead emits a root plan with a parent-authored contract plus a
/// producer/consumer pair over disjoint fileScopes, and drives an automated reviewer that rejects a
/// non-conformant consumer.
/// </summary>
internal sealed class RecursivePlanningLlmHandler : HttpMessageHandler
{
    private readonly bool _peerContractMode;
    private readonly bool _nonConformantConsumer;

    public RecursivePlanningLlmHandler(bool peerContractMode = false, bool nonConformantConsumer = false)
    {
        _peerContractMode = peerContractMode;
        _nonConformantConsumer = nonConformantConsumer;
    }

    // Goal texts — the disambiguation keys.
    public const string RootGoal = "Assemble the widget product";
    public const string CompoundGoal = "Build the Charlie subsystem";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        var step = (messages.GetArrayLength() - 1) / 2;
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";

        var json =
            firstMsg.StartsWith("Begin orchestrating", StringComparison.Ordinal) ? OrchestratorStep(step, firstMsg) :
            firstMsg.StartsWith("Plan work unit", StringComparison.Ordinal) ? PlannerStep(step, firstMsg) :
            firstMsg.StartsWith("Review merge proposal", StringComparison.Ordinal) ? ReviewerStep(step, firstMsg) :
            WorkerStep(step, firstMsg, messages);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    // Orchestrator is a pure service (orchestrator-pure-service.md M2) so this branch is normally never
    // hit — kept as a harmless safety net that just enqueues a planner, mirroring FanOutLlmHandler.
    private static string OrchestratorStep(int step, string firstMsg)
    {
        var wuId = ParseBetween(firstMsg, "Begin orchestrating work unit ", ". Your agent ID is");
        return step switch
        {
            0 => ToolUse("tu-o-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
            1 => ToolUse("tu-o-2", "nm_v1_scheduler_enqueue", new { workUnitId = wuId, profileId = "planner" }),
            _ => EndTurn(),
        };
    }

    private string PlannerStep(int step, string firstMsg)
    {
        var wuId = ParseBetween(firstMsg, "Plan work unit ", ". Your agent ID is");
        var goal = ParseBetween(firstMsg, "Goal: ", "\n\n");

        object planPayload =
            _peerContractMode && goal.Contains("widget", StringComparison.OrdinalIgnoreCase) ? PeerContractRootPlan() :
            goal.Contains("widget", StringComparison.OrdinalIgnoreCase) ? RecursiveRootPlan() :
            goal.Contains("Charlie", StringComparison.OrdinalIgnoreCase) ? CompoundSubPlan() :
            new { slices = Array.Empty<object>() };

        var planJson = JsonSerializer.Serialize(planPayload);

        return step switch
        {
            0 => ToolUse("tu-p-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
            1 => ToolUse("tu-p-2", McpToolNames.ArtifactRecordPlan, new { workUnitId = wuId, planContent = planJson }),
            _ => EndTurn(),
        };
    }

    // Root plan: one compound slice (c1) that will be re-planned, plus a leaf sibling (l1) run directly.
    private static object RecursiveRootPlan() => new
    {
        slices = new object[]
        {
            new
            {
                sliceId = "c1",
                goal = CompoundGoal,
                fileScope = new[] { "src/charlie/" },
                dependsOn = Array.Empty<string>(),
                steps = new[] { "Decompose the Charlie subsystem" },
                kind = "compound",
            },
            new
            {
                sliceId = "l1",
                goal = "Implement module Lima",
                fileScope = new[] { "src/Lima.cs" },
                dependsOn = Array.Empty<string>(),
                steps = new[] { "Create Lima.cs" },
                kind = "leaf",
            },
        },
    };

    // c1's sub-plan: two leaf grandchildren over disjoint scopes. g2 is deliberately marked compound to
    // exercise the depth-cap demotion (at depth 2 with MaxPlanDepth=2 it is forced to a worker).
    private static object CompoundSubPlan() => new
    {
        slices = new object[]
        {
            new
            {
                sliceId = "g1",
                goal = "Implement module Alpha",
                fileScope = new[] { "src/Alpha.cs" },
                dependsOn = Array.Empty<string>(),
                steps = new[] { "Create Alpha.cs" },
                kind = "leaf",
            },
            new
            {
                sliceId = "g2",
                goal = "Implement module Bravo",
                fileScope = new[] { "src/Bravo.cs" },
                dependsOn = Array.Empty<string>(),
                steps = new[] { "Create Bravo.cs" },
                kind = "compound", // will be demoted to a worker at the cap
            },
        },
    };

    // S5.b — a producer/consumer pair over disjoint files bound by an explicit contract.
    private static object PeerContractRootPlan() => new
    {
        slices = new object[]
        {
            new
            {
                sliceId = "api",
                goal = "Implement the user API endpoint (module Alpha)",
                fileScope = new[] { "src/Alpha.cs" },
                dependsOn = Array.Empty<string>(),
                steps = new[] { "Create Alpha.cs serving the contract" },
                kind = "leaf",
                provides = new[] { "c-user" },
            },
            new
            {
                sliceId = "ui",
                goal = "Implement the user page (module Bravo) that calls the API",
                fileScope = new[] { "src/Bravo.cs" },
                dependsOn = Array.Empty<string>(),
                steps = new[] { "Create Bravo.cs calling the contract" },
                kind = "leaf",
                consumes = new[] { "c-user" },
            },
        },
        contracts = new object[]
        {
            new
            {
                contractId = "c-user",
                description = "user endpoint",
                schema = new[] { "GET /api/user -> { id: string, name: string }" },
            },
        },
    };

    private string WorkerStep(int step, string firstMsg, JsonElement messages)
    {
        var taskId = ParseBetween(firstMsg, "Execute task ", " for work unit ");
        var wuId = ParseBetween(firstMsg, "for work unit ", ". Your agent ID is");
        var agentId = ParseBetween(firstMsg, "Your agent ID is ", ".");

        var goal = step >= 1
            ? ExtractFromToolResult(messages, "Goal") ?? ExtractFromToolResult(messages, "goal") ?? ""
            : "";
        var branchId = step >= 1
            ? ExtractFromToolResult(messages, "BranchId") ?? ExtractFromToolResult(messages, "branchId")
            : null;

        var (targetFile, fileContent) = FileForGoal(goal);

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
                agentId,
            }),
            5 => ToolUse("tu-w-6", "nm_v1_merge_validate", new
            {
                proposalId = ExtractFromToolResult(messages, "proposalId") ?? "unknown",
            }),
            _ => EndTurn(),
        };
    }

    private (string file, string content) FileForGoal(string goal)
    {
        if (goal.Contains("Alpha", StringComparison.OrdinalIgnoreCase))
        {
            // In S5.b the Alpha slice is the contract producer.
            var content = _peerContractMode
                ? "class Alpha { public string Get() => \"/api/user -> { id, name }\"; }"
                : "class Alpha {}";
            return ("src/Alpha.cs", content);
        }
        if (goal.Contains("Bravo", StringComparison.OrdinalIgnoreCase))
        {
            // In S5.b the Bravo slice is the consumer; a non-conformant consumer calls an undeclared field.
            var content = _peerContractMode
                ? (_nonConformantConsumer
                    ? "class Bravo { void Use(Alpha a) => a.Get().email(); }"   // 'email' not in the contract
                    : "class Bravo { void Use(Alpha a) => a.Get().name(); }")   // conforms
                : "class Bravo {}";
            return ("src/Bravo.cs", content);
        }
        if (goal.Contains("Lima", StringComparison.OrdinalIgnoreCase))
            return ("src/Lima.cs", "class Lima {}");
        return ("src/Unknown.cs", "class Unknown {}");
    }

    // S5.b — automated reviewer. Rejects the consumer whose kickoff shows a c-user contract it violates
    // (the "email" field the contract doesn't declare); approves everything else. The contract text is
    // present in firstMsg only because S6 plumbs it into the reviewer kickoff.
    private string ReviewerStep(int step, string firstMsg)
    {
        var proposalId = ParseBetween(firstMsg, "Review merge proposal ", " for work unit");
        var mentionsContract = firstMsg.Contains("c-user", StringComparison.Ordinal);
        var reject = _peerContractMode && _nonConformantConsumer && mentionsContract
            && firstMsg.Contains("Bravo", StringComparison.Ordinal);

        return step switch
        {
            0 => ToolUse("tu-r-1", "nm_v1_merge_review", new
            {
                proposalId,
                decision = reject ? "Rejected" : "Approved",
                automated = true,
                verificationResults = reject
                    ? "Consumer calls undeclared field 'email' not present in contract c-user."
                    : "Conforms to declared contract; changes match plan scope.",
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
            stop_reason = "tool_use",
        });

    private static string EndTurn() =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Done." } },
            stop_reason = "end_turn",
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
