using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class ReasoningTools(IOrchestrationDecisionLogService decisionLog)
{
    [McpServerTool(Name = McpToolNames.ReasoningRecord), Description("Record a reasoning commit from an agent's orchestration step.")]
    public async Task<string> RecordAsync(
        string workUnitId,
        string agentId,
        string inputStage,
        string action,
        string? reasoning = null,
        string? projectionSnapshotJson = null,
        string? agentModel = null,
        string? agentProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<PipelineStage>(inputStage, ignoreCase: true, out var stage))
            return McpJson.Error(McpToolNames.ReasoningRecord, $"Invalid stage '{inputStage}'.");

        if (!Enum.TryParse<OrchestrationAction>(action, ignoreCase: true, out var orchestrationAction))
            return McpJson.Error(McpToolNames.ReasoningRecord, $"Invalid action '{action}'.");

        var ev = await decisionLog.RecordAsync(
            workUnitId,
            agentId,
            stage,
            projectionSnapshotJson ?? "{}",
            orchestrationAction,
            [],
            reasoning,
            ct: cancellationToken).ConfigureAwait(false);

        return McpJson.Ok(new
        {
            commitId = ev.EventId,
            workUnitId,
            agentId,
            agentModel,
            agentProvider,
            inputStage = stage.ToString(),
            action = orchestrationAction.ToString(),
            reasoning,
            committedAt = ev.OccurredAt
        });
    }
}