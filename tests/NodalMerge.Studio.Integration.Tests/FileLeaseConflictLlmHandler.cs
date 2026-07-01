using System.Net;
using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Fake HttpMessageHandler scripting exactly two workers fighting over one file path: a
/// "holder" that writes it first and proposes/merges, and a "waiter" whose write collides with
/// the holder's lease, gets queued, and is later auto-resumed once the holder's proposal merges.
/// Holder/waiter ids are set after StudioWebApplication.Build (work units don't exist yet at
/// HttpClient construction time) but before either is enqueued.
///
/// Holder script: workunit.get → workspace.write → merge.propose → merge.validate → end_turn
/// Waiter script: workunit.get → workspace.read → workspace.write → end_turn
///   (workspace.read is unconditional so the waiter's *second*, resumed run — where the file now
///   exists in its branch, copied forward from the holder's merge — satisfies WorkspaceWriteAsync's
///   read-before-overwrite guard; on the waiter's first run the file doesn't exist yet, so the read
///   harmlessly errors and the script proceeds to the write that actually collides.)
///
/// Records every step index seen per work unit, partitioned by conversation (a fresh conversation
/// starts whenever step 0 is observed again) — this is what lets a test assert a conflicted run
/// stopped dead at the tool call that hit the conflict, with no further LLM turn.
/// </summary>
internal sealed class FileLeaseConflictLlmHandler : HttpMessageHandler
{
    public string? HolderWorkUnitId { get; set; }
    public string? HolderBranchId { get; set; }
    public string? WaiterWorkUnitId { get; set; }
    public string? WaiterBranchId { get; set; }
    public string Path { get; set; } = "src/Shared.cs";

    private readonly Dictionary<string, List<List<int>>> _calls = [];

    public IReadOnlyList<IReadOnlyList<int>> CallsFor(string workUnitId)
    {
        lock (_calls)
        {
            return _calls.TryGetValue(workUnitId, out var convos)
                ? convos.Select(c => (IReadOnlyList<int>)[.. c]).ToList()
                : [];
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        var step = (messages.GetArrayLength() - 1) / 2;
        var firstMsg = messages[0].GetProperty("content").GetString() ?? "";
        var wuId = ParseBetween(firstMsg, "for work unit ", ". Your agent ID is");
        var agentId = ParseBetween(firstMsg, "Your agent ID is ", ".");

        RecordCall(wuId, step);

        var json = wuId == HolderWorkUnitId
            ? HolderStep(step, wuId, agentId, messages)
            : wuId == WaiterWorkUnitId
                ? WaiterStep(step, wuId)
                : EndTurn();

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private string HolderStep(int step, string wuId, string agentId, JsonElement messages) => step switch
    {
        0 => ToolUse("h-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
        1 => ToolUse("h-2", "nm_v1_workspace_write", new
        {
            branchId = HolderBranchId,
            workUnitId = wuId,
            path = Path,
            content = "from-holder",
        }),
        2 => ToolUse("h-3", "nm_v1_merge_propose", new
        {
            sourceBranch = HolderBranchId,
            targetBranch = "main",
            summary = "Holder's change",
            workUnitId = wuId,
            agentId,
        }),
        3 => ToolUse("h-4", "nm_v1_merge_validate", new
        {
            proposalId = ExtractFromToolResult(messages, "proposalId") ?? "unknown",
        }),
        _ => EndTurn(),
    };

    private string WaiterStep(int step, string wuId) => step switch
    {
        0 => ToolUse("w-1", "nm_v1_workunit_get", new { workUnitId = wuId }),
        1 => ToolUse("w-2", "nm_v1_workspace_read", new
        {
            branchId = WaiterBranchId,
            workUnitId = wuId,
            path = Path,
        }),
        2 => ToolUse("w-3", "nm_v1_workspace_write", new
        {
            branchId = WaiterBranchId,
            workUnitId = wuId,
            path = Path,
            content = "from-waiter",
        }),
        _ => EndTurn(),
    };

    private void RecordCall(string wuId, int step)
    {
        lock (_calls)
        {
            if (!_calls.TryGetValue(wuId, out var convos))
            {
                convos = [];
                _calls[wuId] = convos;
            }

            if (step == 0 || convos.Count == 0)
                convos.Add([]);

            convos[^1].Add(step);
        }
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
                catch { /* malformed tool result — skip */ }
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
